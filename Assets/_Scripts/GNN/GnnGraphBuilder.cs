using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Sahnedeki etiketli tehlike kutusu (Vinc / İskele / Electiric trigger'ları).</summary>
public struct GnnHazardZone
{
    public BoxCollider Box;
    public GnnHazardType Type;

    /// <summary>Kutu devre dışı olsa da çalışır (Collider.bounds kapalı collider'da boş döner).</summary>
    public bool Contains(Vector3 worldPoint)
    {
        Vector3 local = Box.transform.InverseTransformPoint(worldPoint) - Box.center;
        Vector3 half = Box.size * 0.5f;
        return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
    }

    public float Distance(Vector3 worldPoint)
    {
        Vector3 local = Box.transform.InverseTransformPoint(worldPoint) - Box.center;
        Vector3 half = Box.size * 0.5f;
        Vector3 clamped = new Vector3(
            Mathf.Clamp(local.x, -half.x, half.x),
            Mathf.Clamp(local.y, -half.y, half.y),
            Mathf.Clamp(local.z, -half.z, half.z));
        return Vector3.Distance(worldPoint, Box.transform.TransformPoint(clamped + Box.center));
    }

    public Bounds WorldBounds()
    {
        var b = new Bounds(Box.transform.TransformPoint(Box.center), Vector3.zero);
        Vector3 h = Box.size * 0.5f;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
            b.Encapsulate(Box.transform.TransformPoint(Box.center + corner));
        }
        return b;
    }
}

/// <summary>
/// İnşaat sahasının zemininden GNN grafı kurar. Eğitim verisi ve runtime grafı aynı kodla
/// Editor'de üretilir; böylece özellikler birebir aynı hesaplanır.
///
/// ÖNEMLİ: Vinc / İskele / Electiric trigger kutuları özellik olarak KULLANILMAZ (trigger'lar
/// fizik sorgularından, altındaki objeler isim aramasından hariç tutulur). Kutular yalnızca
/// eğitimde etiket kaynağıdır (ComputeLabels).
/// </summary>
public sealed class GnnGraphBuilder
{
    public static readonly string[] FeatureNames =
    {
        "crane_distance",         // 0: en yakın vinç parçasına uzaklık
        "scaffold_distance",      // 1: en yakın iskele parçasına uzaklık
        "electric_distance",      // 2: en yakın elektrik kaynağına (pano, jeneratör, kablo) uzaklık
        "overhead_clearance",     // 3: baş üstündeki ilk yapıya uzaklık (1 = açık gökyüzü)
        "min_clearance",          // 4: 8 yönde en yakın engel
        "mean_clearance",         // 5: 8 yönde ortalama engel uzaklığı
        "openness",               // 6: açık yön oranı
        "tall_object_height",     // 7: yakındaki en yüksek yapı
        "drop_height",            // 8: yakındaki en büyük zemin düşüşü (düşme kenarı)
        "loose_objects"           // 9: yakında yüksekte duran serbest (düşebilecek) eşya sayısı
    };

    public static readonly string[] ClassNames = { "Safe", "Caution", "Danger" };
    public static int FeatureCount => FeatureNames.Length;

    /// <summary>Etiket kaynağı olan tag'ler ve karşılık gelen tehlike türleri.</summary>
    public static readonly (string tag, GnnHazardType type)[] HazardTags =
    {
        ("Vinc", GnnHazardType.Crane),
        ("İskele", GnnHazardType.Scaffold),
        ("Electiric", GnnHazardType.Electric)
    };

    private const float ChestHeight = 1.2f;
    private const float WaistHeight = 0.8f;
    private const float KneeHeight = 0.4f;
    private const float HeadHeight = 2.0f;

    private static readonly Vector3[] Dirs8 =
    {
        new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1),
        new Vector3(0.7071f, 0, 0.7071f), new Vector3(-0.7071f, 0, 0.7071f),
        new Vector3(0.7071f, 0, -0.7071f), new Vector3(-0.7071f, 0, -0.7071f)
    };

    private static readonly Vector2Int[] ForwardNeighbors =
    {
        new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, -1)
    };

    private readonly GnnGraphSettings settings;
    private readonly HashSet<Collider> ignored = new HashSet<Collider>();
    private readonly List<Bounds>[] hazardSources = { new List<Bounds>(), new List<Bounds>(), new List<Bounds>() };
    private readonly HashSet<Rigidbody> looseSet = new HashSet<Rigidbody>();
    private readonly RaycastHit[] hitBuffer = new RaycastHit[64];
    private readonly Collider[] colliderBuffer = new Collider[512];
    private readonly Bounds area;
    private readonly float groundY;
    private readonly float minWalkableNormalY;

    public GnnGraph Graph { get; } = new GnnGraph();
    public bool HasArea { get; }

    /// <param name="siteArea">Grafın kurulacağı alan (dünya). Boyutu sıfırsa graf kurulmaz.</param>
    /// <param name="ignoreRoots">Collider'ları ve renderer'ları yok sayılacak objeler (oyuncu, tehlike kutuları)</param>
    /// <param name="groundReference">Zemin yüksekliğini belirlemek için zemindeki bir nokta (ör. oyuncu)</param>
    public GnnGraphBuilder(GnnGraphSettings settings, Bounds siteArea, IEnumerable<Transform> ignoreRoots,
        Vector3 groundReference)
    {
        this.settings = settings;
        area = siteArea;
        HasArea = siteArea.size.x > 0f && siteArea.size.z > 0f;
        minWalkableNormalY = Mathf.Cos(settings.maxSlope * Mathf.Deg2Rad);

        var roots = new List<Transform>();
        foreach (Transform root in ignoreRoots)
        {
            if (root == null) continue;
            roots.Add(root);
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
                ignored.Add(c);
        }

        groundY = FindGroundHeight(groundReference);
        if (HasArea) CollectHazardSources(roots);

        Graph.FeatureCount = FeatureCount;
        Graph.CellSize = settings.cellSize;
        Graph.GridOrigin = new Vector3(area.min.x, groundY, area.min.z);
        Graph.GridOffset = settings.gridOffset;
    }

    /// <summary>Hazard kaynağı bulunan tür sayısı (0 ise isim anahtar kelimeleri sahneyle uyuşmuyor).</summary>
    public int HazardSourceCount(GnnHazardType type) => hazardSources[(int)type].Count;

    /// <summary>Grafı birkaç kareye yayarak kurar.</summary>
    public IEnumerator Build(int nodesPerStep)
    {
        if (!HasArea) yield break;
        Physics.SyncTransforms();

        float cs = Graph.CellSize;
        int countX = Mathf.CeilToInt(area.size.x / cs);
        int countZ = Mathf.CeilToInt(area.size.z / cs);
        int budget = 0;

        // 1) Düğümler: yürünebilir ve boş zemin hücreleri
        for (int ix = 0; ix < countX; ix++)
        {
            for (int iz = 0; iz < countZ; iz++)
            {
                var cell = new Vector2Int(ix, iz);
                if (TrySampleGround(CellCenter(cell), out Vector3 floorPoint))
                {
                    Graph.CellToNode[cell] = Graph.Positions.Count;
                    Graph.Positions.Add(floorPoint);
                    Graph.Cells.Add(cell);
                }

                if (++budget >= nodesPerStep * 2) { budget = 0; yield return null; }
            }
        }

        // 2) Düğüm özellikleri
        int n = Graph.NodeCount;
        int f = FeatureCount;
        Graph.Features = new float[n * f];
        for (int i = 0; i < n; i++)
        {
            ComputeFeatures(Graph.Positions[i], Graph.Features, i * f);
            if (++budget >= nodesPerStep) { budget = 0; yield return null; }
        }

        // 3) Kenarlar: arada engel olmayan 8-komşular
        var adjacency = new List<int>[n];
        for (int i = 0; i < n; i++) adjacency[i] = new List<int>(8);
        for (int i = 0; i < n; i++)
        {
            Vector2Int cell = Graph.Cells[i];
            foreach (Vector2Int d in ForwardNeighbors)
            {
                if (!Graph.CellToNode.TryGetValue(cell + d, out int j)) continue;
                if (!IsConnected(Graph.Positions[i], Graph.Positions[j])) continue;
                adjacency[i].Add(j);
                adjacency[j].Add(i);
            }
            if (++budget >= nodesPerStep * 2) { budget = 0; yield return null; }
        }

        Graph.NeighborOffsets = new int[n + 1];
        for (int i = 0; i < n; i++) Graph.NeighborOffsets[i + 1] = Graph.NeighborOffsets[i] + adjacency[i].Count;
        Graph.NeighborIndices = new int[Graph.NeighborOffsets[n]];
        for (int i = 0; i < n; i++) adjacency[i].CopyTo(Graph.NeighborIndices, Graph.NeighborOffsets[i]);
    }

    public GnnGraph BuildImmediate()
    {
        IEnumerator routine = Build(int.MaxValue);
        while (routine.MoveNext()) { }
        return Graph;
    }

    private Vector3 CellCenter(Vector2Int cell)
    {
        float cs = Graph.CellSize;
        return new Vector3(
            Graph.GridOrigin.x + (cell.x + 0.5f + settings.gridOffset.x) * cs,
            groundY,
            Graph.GridOrigin.z + (cell.y + 0.5f + settings.gridOffset.y) * cs);
    }

    /// <summary>
    /// Hücredeki zemin noktası: zemin yüksekliği bandındaki en üst yürünebilir yüzey.
    /// Bant, iskele platformu / çatı gibi oyuncunun çıkamayacağı yüzeyleri dışarıda bırakır.
    /// </summary>
    private bool TrySampleGround(Vector3 center, out Vector3 floorPoint)
    {
        floorPoint = center;
        float top = groundY + 2.5f;
        Vector3 origin = new Vector3(center.x, top, center.z);
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, hitBuffer, 6f, settings.layerMask, QueryTriggerInteraction.Ignore);

        bool found = false;
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = hitBuffer[i];
            if (ignored.Contains(h.collider) || h.normal.y < minWalkableNormalY) continue;
            if (h.point.y < groundY - 3f || h.point.y > groundY + 1.5f) continue;
            if (!found || h.point.y > floorPoint.y) { floorPoint = h.point; found = true; }
        }
        if (!found) return false;

        // Hücre bir yapı / eşya tarafından dolduruluyorsa düğüm değil
        float r = settings.bodyRadius;
        Vector3 bottom = floorPoint + Vector3.up * (r + 0.15f);
        Vector3 head = floorPoint + Vector3.up * 1.6f;
        return OverlapCapsule(bottom, head, r) == 0;
    }

    private void ComputeFeatures(Vector3 fp, float[] output, int offset)
    {
        Vector3 up = Vector3.up;

        // 0-2: Tehlike kaynaklarına uzaklık
        Vector3 chest = fp + up * ChestHeight;
        float hazardProbe = settings.hazardProbeDistance;
        for (int t = 0; t < 3; t++)
        {
            float best = hazardProbe;
            foreach (Bounds b in hazardSources[t])
                best = Mathf.Min(best, Mathf.Sqrt(b.SqrDistance(chest)));
            output[offset + t] = best / hazardProbe;
        }

        // 3: Baş üstü
        float upLength = settings.overheadProbeDistance;
        float overhead = NearestHit(fp + up * HeadHeight, up, upLength, out RaycastHit upHit) ? upHit.distance / upLength : 1f;

        // 4-6: Çevre açıklığı
        Vector3 waist = fp + up * WaistHeight;
        float probe = settings.clearanceProbeDistance;
        float minClear = probe, sumClear = 0f;
        int open = 0;
        foreach (Vector3 dir in Dirs8)
        {
            float d = NearestHit(waist, dir, probe, out RaycastHit h) ? h.distance : probe;
            minClear = Mathf.Min(minClear, d);
            sumClear += d;
            if (d >= probe * 0.5f) open++;
        }

        // 7, 9: Yakındaki yüksek yapılar ve düşebilecek serbest eşyalar
        float tall = 0f;
        looseSet.Clear();
        int count = OverlapSphere(chest, settings.neighborRadius);
        for (int k = 0; k < count; k++)
        {
            Collider c = colliderBuffer[k];
            if (c is TerrainCollider) continue;

            Bounds b = c.bounds;
            if (b.min.y < fp.y + 2.5f) tall = Mathf.Max(tall, b.max.y - fp.y);

            Rigidbody rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic && b.center.y - fp.y > 1.5f)
                looseSet.Add(rb);
        }

        // 8: Düşme kenarı
        float drop = 0f;
        const float maxDrop = 3f;
        foreach (Vector3 dir in Dirs8)
        {
            Vector3 from = fp + dir * settings.dropProbeDistance + up * 1f;
            float d = NearestHit(from, Vector3.down, 1f + maxDrop, out RaycastHit h) ? (fp.y - h.point.y) : maxDrop;
            drop = Mathf.Max(drop, d);
        }

        output[offset + 3] = overhead;
        output[offset + 4] = minClear / probe;
        output[offset + 5] = sumClear / Dirs8.Length / probe;
        output[offset + 6] = open / (float)Dirs8.Length;
        output[offset + 7] = Mathf.Clamp01(tall / 20f);
        output[offset + 8] = Mathf.Clamp01(drop / maxDrop);
        output[offset + 9] = Mathf.Clamp01(looseSet.Count / 5f);
    }

    private bool IsConnected(Vector3 a, Vector3 b)
    {
        if (Mathf.Abs(a.y - b.y) > settings.maxStep) return false;
        Vector3 from = a + Vector3.up * KneeHeight;
        Vector3 delta = b - a;
        return !NearestHit(from, delta.normalized, delta.magnitude, out _);
    }

    private float FindGroundHeight(Vector3 reference)
    {
        Vector3 origin = reference + Vector3.up * 1f;
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, hitBuffer, 20f, settings.layerMask, QueryTriggerInteraction.Ignore);
        float best = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = hitBuffer[i];
            if (ignored.Contains(h.collider) || h.normal.y < minWalkableNormalY) continue;
            best = Mathf.Max(best, h.point.y);
        }
        return best > float.MinValue ? best : reference.y;
    }

    private bool NearestHit(Vector3 origin, Vector3 dir, float length, out RaycastHit best)
    {
        int count = Physics.RaycastNonAlloc(origin, dir, hitBuffer, length, settings.layerMask, QueryTriggerInteraction.Ignore);
        best = default;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = hitBuffer[i];
            if (ignored.Contains(h.collider)) continue;
            if (!found || h.distance < best.distance) { best = h; found = true; }
        }
        return found;
    }

    /// <summary>Yoksayılmayan collider'ları buffer'ın başına toplar.</summary>
    private int OverlapSphere(Vector3 center, float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(center, radius, colliderBuffer, settings.layerMask, QueryTriggerInteraction.Ignore);
        return KeepNotIgnored(count);
    }

    private int OverlapCapsule(Vector3 a, Vector3 b, float radius)
    {
        int count = Physics.OverlapCapsuleNonAlloc(a, b, radius, colliderBuffer, settings.layerMask, QueryTriggerInteraction.Ignore);
        return KeepNotIgnored(count);
    }

    private int KeepNotIgnored(int count)
    {
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            if (ignored.Contains(colliderBuffer[i])) continue;
            colliderBuffer[kept++] = colliderBuffer[i];
        }
        return kept;
    }

    private void CollectHazardSources(List<Transform> ignoreRoots)
    {
        Bounds searchArea = area;
        searchArea.Expand(new Vector3(2f, 0f, 2f) * settings.hazardProbeDistance);
        string[][] keywords = { settings.craneKeywords, settings.scaffoldKeywords, settings.electricKeywords };

        foreach (MeshRenderer r in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (!r.enabled || !searchArea.Intersects(r.bounds) || IsUnder(r.transform, ignoreRoots)) continue;
            for (int t = 0; t < 3; t++)
            {
                if (MatchesAny(r, keywords[t])) { hazardSources[t].Add(r.bounds); break; }
            }
        }
    }

    /// <summary>Renderer'ın kendi adı, üst objelerinin adı veya materyali anahtar kelimeyi içeriyor mu.</summary>
    private static bool MatchesAny(Renderer r, string[] keywords)
    {
        for (Transform t = r.transform; t != null; t = t.parent)
        {
            if (ContainsKeyword(t.name, keywords)) return true;
        }
        foreach (Material m in r.sharedMaterials)
        {
            if (m != null && ContainsKeyword(m.name, keywords)) return true;
        }
        return false;
    }

    private static bool ContainsKeyword(string text, string[] keywords)
    {
        string lower = text.ToLowerInvariant();
        foreach (string k in keywords)
        {
            if (!string.IsNullOrEmpty(k) && lower.Contains(k.ToLowerInvariant())) return true;
        }
        return false;
    }

    private static bool IsUnder(Transform t, List<Transform> roots)
    {
        foreach (Transform root in roots)
        {
            if (t.IsChildOf(root)) return true;
        }
        return false;
    }

    /// <summary>Sahnedeki (kapalı olanlar dahil) Vinc / İskele / Electiric tehlike kutuları.</summary>
    public static List<GnnHazardZone> FindHazardZones()
    {
        var zones = new List<GnnHazardZone>();
        foreach (BoxCollider box in Object.FindObjectsOfType<BoxCollider>(true))
        {
            if (!box.isTrigger) continue;
            string tag = box.gameObject.tag;
            foreach (var (hazardTag, type) in HazardTags)
            {
                if (tag == hazardTag) zones.Add(new GnnHazardZone { Box = box, Type = type });
            }
        }
        return zones;
    }

    /// <summary>
    /// Eğitim etiketleri: kutunun içi Danger, kutuya cautionDistance'tan yakın Caution, kalanı Safe.
    /// </summary>
    public static int[] ComputeLabels(GnnGraph graph, List<GnnHazardZone> zones, float cautionDistance)
    {
        var labels = new int[graph.NodeCount];
        for (int i = 0; i < graph.NodeCount; i++)
        {
            Vector3 p = graph.Positions[i] + Vector3.up * 1f;
            int label = (int)GnnRiskClass.Safe;
            foreach (GnnHazardZone z in zones)
            {
                if (z.Contains(p)) { label = (int)GnnRiskClass.Danger; break; }
                if (z.Distance(p) <= cautionDistance) label = (int)GnnRiskClass.Caution;
            }
            labels[i] = label;
        }
        return labels;
    }
}
