using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public struct GnnPrediction
{
    public bool IsValid;
    public float SafeProbability;
    public float CautionProbability;
    public float DangerProbability;
    public GnnRiskClass RiskClass;
    public GnnHazardType Hazard;

    /// <summary>0 (tamamen güvenli) – 1 (tamamen tehlikeli) arası sürekli risk skoru.</summary>
    public float RiskScore => Mathf.Clamp01(DangerProbability + 0.5f * CautionProbability);
}

/// <summary>
/// İnşaat sahası risk haritası. Saha sabit olduğu için graf Editor'de bir kez çıkarılıp
/// Resources/GNN/gnn_graph_&lt;sahne&gt;.json olarak kaydedilir; runtime'da sahne taranmaz,
/// yalnızca eğitilmiş GNN sahne açılışında bir kez çalıştırılır (Quest'te fizik maliyeti yok).
/// Puanlama sırasında oyuncunun konumuna en yakın düğümün tahmini döndürülür.
/// </summary>
public class GnnRiskPredictor : MonoBehaviour
{
    public const string DefaultWeightsResource = "GNN/gnn_weights";
    public const string GraphResourcePrefix = "GNN/gnn_graph_";

    public static GnnRiskPredictor Instance { get; private set; }

    [Header("Model ve Graf")]
    [Tooltip("Boşsa Resources/GNN/gnn_weights.json yüklenir")]
    [SerializeField] private TextAsset weightsJson;
    [Tooltip("Boşsa Resources/GNN/gnn_graph_<sahne>.json yüklenir (Tools > GNN ile üretilir)")]
    [SerializeField] private TextAsset graphJson;

    [Header("Graf Çıkarma (yalnızca Editor dışa aktarımında kullanılır)")]
    [Tooltip("Boşsa alan, tehlike kutularının çevresi Site Margin kadar genişletilerek bulunur")]
    [SerializeField] private BoxCollider siteArea;
    [SerializeField] private GnnGraphSettings graphSettings = new GnnGraphSettings();

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;
    [Tooltip("Açık: oyunda kullanılan karar (en olası sınıf). Kapalı: olasılıkların karışımı (sarımsı = kararsız)")]
    [SerializeField] private bool gizmoShowsDecision = true;
    [SerializeField] private bool showDebugLogs = true;

    private GnnModel model;
    private GnnGraph graph;
    private float[] probabilities;
    private int[] previewLabels;

    public bool IsReady => graph != null && probabilities != null;
    public GnnGraph Graph => graph;
    public GnnGraphSettings GraphSettings => graphSettings;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        LoadModel();
        LoadGraph();
        PredictNow();
    }

    public void PredictNow()
    {
        if (model == null || graph == null) return;

        float start = Time.realtimeSinceStartup;
        probabilities = model.Predict(graph);

        if (showDebugLogs)
        {
            int[] counts = new int[3];
            for (int i = 0; i < graph.NodeCount; i++) counts[(int)ArgMax(i)]++;
            Debug.Log($"<color=cyan>[GNN] Risk haritası hesaplandı ({(Time.realtimeSinceStartup - start) * 1000f:F1} ms, {graph.NodeCount} düğüm): " +
                      $"Güvenli {counts[0]}, Dikkat {counts[1]}, Tehlikeli {counts[2]}.</color>");
        }
    }

    /// <summary>Konumun risk tahmini. Graf dışındaysa (zemin dışı, yapının içi) IsValid = false.</summary>
    public GnnPrediction GetPrediction(Vector3 worldPosition)
    {
        var result = new GnnPrediction { Hazard = GnnHazardType.None };
        if (!IsReady) return result;

        int node = FindNearestNode(worldPosition);
        if (node < 0) return result;

        result.IsValid = true;
        result.SafeProbability = probabilities[node * 3 + 0];
        result.CautionProbability = probabilities[node * 3 + 1];
        result.DangerProbability = probabilities[node * 3 + 2];
        result.RiskClass = ArgMax(node);
        result.Hazard = NearestHazard(node);
        return result;
    }

    private GnnRiskClass ArgMax(int node)
    {
        int best = 0;
        for (int c = 1; c < 3; c++)
        {
            if (probabilities[node * 3 + c] > probabilities[node * 3 + best]) best = c;
        }
        return (GnnRiskClass)best;
    }

    /// <summary>Düğümün özelliklerinden en yakın tehlike kaynağı (özellik 0-2: vinç/iskele/elektrik uzaklığı).</summary>
    private GnnHazardType NearestHazard(int node)
    {
        int f = graph.FeatureCount;
        int best = -1;
        float bestDist = 1f; // 1 = arama mesafesinde kaynak yok
        for (int t = 0; t < 3; t++)
        {
            float d = graph.Features[node * f + t];
            if (d < bestDist) { bestDist = d; best = t; }
        }
        return (GnnHazardType)best;
    }

    private int FindNearestNode(Vector3 worldPosition)
    {
        Vector2Int c = graph.CellOf(worldPosition);
        const int searchRadius = 2;
        int best = -1;
        float bestDist = float.MaxValue;
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dz = -searchRadius; dz <= searchRadius; dz++)
            {
                if (!graph.CellToNode.TryGetValue(new Vector2Int(c.x + dx, c.y + dz), out int node)) continue;
                Vector3 q = graph.Positions[node];
                float d = (q.x - worldPosition.x) * (q.x - worldPosition.x) + (q.z - worldPosition.z) * (q.z - worldPosition.z);
                if (d < bestDist) { bestDist = d; best = node; }
            }
        }
        return best;
    }

    private void LoadModel()
    {
        TextAsset asset = weightsJson != null ? weightsJson : Resources.Load<TextAsset>(DefaultWeightsResource);
        if (asset == null)
        {
            Debug.LogError($"[GNN] Model ağırlıkları bulunamadı (Resources/{DefaultWeightsResource}.json). Önce GNN/train_gnn.py ile modeli eğitin.");
            return;
        }

        model = GnnModel.FromJson(asset.text, out string error);
        if (model == null) Debug.LogError("[GNN] " + error);
    }

    private void LoadGraph()
    {
        string resource = GraphResourceName(SceneManager.GetActiveScene().name);
        TextAsset asset = graphJson != null ? graphJson : Resources.Load<TextAsset>(resource);
        if (asset == null)
        {
            Debug.LogError($"[GNN] Graf bulunamadı (Resources/{resource}.json). Unity'de Tools > GNN > Eğitim Verisini ve Grafı Dışa Aktar çalıştırın.");
            return;
        }

        var export = JsonUtility.FromJson<GnnGraphExport>(asset.text);
        if (export == null || export.numFeatures != GnnGraphBuilder.FeatureCount)
        {
            Debug.LogError("[GNN] Graf dosyası güncel değil (özellik sayısı uyuşmuyor). Grafı yeniden dışa aktarın.");
            return;
        }
        graph = GnnGraph.FromExport(export);
    }

    /// <summary>Sahne adını Resources yolunda güvenli ASCII'ye çevirir (ör. "Sınav" → "Sinav").</summary>
    public static string GraphResourceName(string sceneName)
    {
        var sb = new System.Text.StringBuilder(GraphResourcePrefix);
        foreach (char ch in sceneName)
        {
            switch (ch)
            {
                case 'ı': sb.Append('i'); break;
                case 'İ': sb.Append('I'); break;
                case 'ş': sb.Append('s'); break;
                case 'Ş': sb.Append('S'); break;
                case 'ğ': sb.Append('g'); break;
                case 'Ğ': sb.Append('G'); break;
                case 'ü': sb.Append('u'); break;
                case 'Ü': sb.Append('U'); break;
                case 'ö': sb.Append('o'); break;
                case 'Ö': sb.Append('O'); break;
                case 'ç': sb.Append('c'); break;
                case 'Ç': sb.Append('C'); break;
                default: sb.Append(ch < 128 && (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') ? ch : '_'); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Editor dışa aktarıcısı da aynı builder'ı kullanır.</summary>
    public GnnGraphBuilder CreateBuilder(GnnGraphSettings settings, List<GnnHazardZone> zones)
    {
        var ignoreRoots = new List<Transform>();
        Vector3 groundReference = Vector3.zero;
        foreach (GameObject go in GameObject.FindGameObjectsWithTag("Player"))
        {
            ignoreRoots.Add(go.transform);
            if (go.GetComponent<CapsuleCollider>() != null) groundReference = go.transform.position;
        }
        foreach (GnnHazardZone z in zones) ignoreRoots.Add(z.Box.transform);

        return new GnnGraphBuilder(settings, ComputeSiteArea(settings, zones), ignoreRoots, groundReference);
    }

    private Bounds ComputeSiteArea(GnnGraphSettings settings, List<GnnHazardZone> zones)
    {
        if (siteArea != null) return siteArea.bounds;
        if (zones.Count == 0) return default;

        Bounds b = zones[0].WorldBounds();
        foreach (GnnHazardZone z in zones) b.Encapsulate(z.WorldBounds());
        b.Expand(new Vector3(2f, 0f, 2f) * settings.siteMargin);
        return b;
    }

    [ContextMenu("GNN: Grafı kur ve etiketleri göster (Editor)")]
    private void PreviewLabels()
    {
        List<GnnHazardZone> zones = GnnGraphBuilder.FindHazardZones();
        graph = CreateBuilder(graphSettings, zones).BuildImmediate();
        previewLabels = GnnGraphBuilder.ComputeLabels(graph, zones, graphSettings.cautionDistance);
        probabilities = null;
        Debug.Log($"[GNN] Önizleme: {graph.NodeCount} düğüm, {graph.EdgeCount / 2} kenar, {zones.Count} tehlike kutusu.");
    }

    [ContextMenu("GNN: Kayıtlı grafla modeli çalıştır (Editor)")]
    private void PreviewPrediction()
    {
        previewLabels = null;
        LoadModel();
        LoadGraph();
        PredictNow();
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos || graph == null) return;

        float size = graph.CellSize * 0.6f;
        Vector3 cube = new Vector3(size, size * 0.15f, size);
        for (int i = 0; i < graph.NodeCount; i++)
        {
            Color color;
            if (probabilities != null && gizmoShowsDecision)
            {
                GnnRiskClass c = ArgMax(i);
                color = c == GnnRiskClass.Safe ? Color.green : c == GnnRiskClass.Danger ? Color.red : Color.yellow;
            }
            else if (probabilities != null)
            {
                float safe = probabilities[i * 3], danger = probabilities[i * 3 + 2];
                color = new Color(danger, safe, 0.15f, 0.85f) + new Color(0.5f, 0.5f, 0f, 0f) * probabilities[i * 3 + 1];
            }
            else if (previewLabels != null)
            {
                color = previewLabels[i] == 0 ? Color.green : previewLabels[i] == 2 ? Color.red : Color.yellow;
            }
            else continue;

            Gizmos.color = color;
            Gizmos.DrawCube(graph.Positions[i] + Vector3.up * size * 0.1f, cube);
        }
    }
}
