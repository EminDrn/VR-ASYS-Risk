using UnityEngine;

/// <summary>
/// Sınav sahnesinde tehlikeli alan cezasını GNN risk haritasından verir. Etkinken
/// WrongPlaceCollider kutuları ceza vermez (çift ceza olmasın); kutular yalnızca eğitim etiketi olarak kalır.
///
/// Her tehlikenin şiddetine göre bir puan bütçesi vardır (Fine-Kinney şiddet ölçeği: elektrik / vinç
/// öldürücü, iskele kalıcı hasar). Bir tehlikenin toplam cezası:
///
///   ceza = bütçe × [ girişPayı × w_giriş + (1 − girişPayı) × (1 − e^(−E / τ)) ] × KKD çarpanı
///
///   w_giriş : ilk girişteki GNN tehlike olasılığı
///   E       : bölgede geçen, GNN tehlike olasılığıyla ağırlıklandırılmış süre (s)
///   τ       : maruziyet zaman sabiti
///   KKD     : ekipman eksikse 1.5, tamsa 1
///
/// Ceza bütçeyi (× KKD çarpanı) aşamaz; bölgeden çıkıp girmek giriş payını tekrar kesmez, süre birikmeye devam eder.
/// Elektrik bölgesi şalterler kapatılınca (Electiric kutusu kapanınca) tehlike sayılmaz.
/// </summary>
public class GnnRiskScorer : MonoBehaviour
{
    private static GnnRiskScorer active;

    /// <summary>
    /// true iken WrongPlaceCollider puan düşmez. Model / graf yüklenemediyse false kalır,
    /// böylece eski kutu cezaları devreye girer.
    /// </summary>
    public static bool IsScoring => active != null && active.riskPredictor != null && active.riskPredictor.IsReady;

    [Header("Referanslar (boşsa otomatik bulunur)")]
    [SerializeField] private GnnRiskPredictor riskPredictor;
    [Tooltip("Konumu puanlanan obje. Boşsa ana kamera (oyuncunun başı)")]
    [SerializeField] private Transform trackedTarget;
    [Tooltip("Şalterler kapanınca EnergyManager'ın kapattığı Electiric kutusu. Boşsa tag ile bulunur.")]
    [SerializeField] private GameObject electricZone;

    [Header("Tehlike Bütçeleri (ekipman tamken en fazla ceza)")]
    [SerializeField] private float craneBudget = 40f;
    [SerializeField] private float scaffoldBudget = 30f;
    [SerializeField] private float electricBudget = 40f;

    [Header("Ceza Eğrisi")]
    [Tooltip("Bütçenin girişte hemen kesilen payı")]
    [SerializeField, Range(0f, 1f)] private float entryShare = 0.4f;
    [Tooltip("Maruziyet zaman sabiti (s): bu kadar kalınca kalan payın %63'ü kesilmiş olur")]
    [SerializeField] private float exposureTimeConstant = 5f;
    [Tooltip("Kişisel koruyucu donanım eksikse tehlike cezaları bu katsayıyla çarpılır")]
    [SerializeField] private float missingPpeMultiplier = 1.5f;
    [Tooltip("Dikkat bölgesine giriş cezası (0 = yalnızca uyarı)")]
    [SerializeField] private int cautionPenalty = 0;

    [Header("Zamanlama")]
    [SerializeField] private float updateInterval = 0.5f;
    [Tooltip("Dikkat bölgesinden çıkıp bu süreden (s) önce tekrar girilirse yeniden uyarı / ceza verilmez")]
    [SerializeField] private float reentryCooldown = 3f;

    [Header("Uyarı Sesi (isteğe bağlı)")]
    [SerializeField] private AudioSource warningSound;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;

    /// <summary>Tehlike başına birikmiş durum (indeks = GnnHazardType).</summary>
    private struct HazardExposure
    {
        public bool Entered;
        public float EntryWeight;  // ilk girişteki tehlike olasılığı
        public float Exposure;     // olasılıkla ağırlıklı süre (s)
        public int Deducted;       // bu tehlike için şimdiye kadar düşülen puan
    }

    private readonly HazardExposure[] hazards = new HazardExposure[3];
    private float nextUpdate;
    private float lastTick = float.NegativeInfinity;
    private GnnRiskClass currentClass = GnnRiskClass.Safe;
    private float cautionLeftAt = float.NegativeInfinity;

    private void Start()
    {
        if (riskPredictor == null) riskPredictor = GnnRiskPredictor.Instance;
        if (riskPredictor == null) riskPredictor = FindObjectOfType<GnnRiskPredictor>();
        if (riskPredictor == null) riskPredictor = gameObject.AddComponent<GnnRiskPredictor>();
        if (trackedTarget == null && Camera.main != null) trackedTarget = Camera.main.transform;
        if (electricZone == null) electricZone = FindHazardObject("Electiric");
    }

    private void OnEnable() => active = this;

    private void OnDisable()
    {
        if (active == this) active = null;
    }

    private void Update()
    {
        if (Time.time < nextUpdate) return;
        nextUpdate = Time.time + updateInterval;

        float dt = Mathf.Min(Time.time - lastTick, updateInterval * 2f);
        lastTick = Time.time;

        if (!IsActive() || trackedTarget == null || !riskPredictor.IsReady) return;

        GnnPrediction p = riskPredictor.GetPrediction(trackedTarget.position);
        if (!p.IsValid) return;

        GnnRiskClass riskClass = p.RiskClass;
        GnnHazardType hazard = p.Hazard;
        if (hazard == GnnHazardType.Electric && !IsElectricLive()) riskClass = GnnRiskClass.Safe;

        GnnRiskClass previous = currentClass;
        currentClass = riskClass;

        if (riskClass == GnnRiskClass.Danger && hazard != GnnHazardType.None)
        {
            UpdateHazard(p, hazard, entering: previous != GnnRiskClass.Danger, dt);
        }
        else if (riskClass == GnnRiskClass.Caution && previous == GnnRiskClass.Safe)
        {
            if (Time.time - cautionLeftAt >= reentryCooldown) EnterCaution(p, hazard);
        }

        if (previous == GnnRiskClass.Caution && riskClass == GnnRiskClass.Safe) cautionLeftAt = Time.time;
    }

    private void UpdateHazard(GnnPrediction p, GnnHazardType hazard, bool entering, float dt)
    {
        ref HazardExposure h = ref hazards[(int)hazard];
        float w = p.DangerProbability;

        if (!h.Entered)
        {
            h.Entered = true;
            h.EntryWeight = w;
            if (warningSound != null) warningSound.Play();
        }
        else if (entering)
        {
            if (warningSound != null) warningSound.Play();
        }
        else
        {
            h.Exposure += w * dt;
        }

        float budget = Budget(hazard) * PpeMultiplier();
        float target = budget * (entryShare * h.EntryWeight + (1f - entryShare) * (1f - Mathf.Exp(-h.Exposure / exposureTimeConstant)));
        int penalty = Mathf.RoundToInt(target) - h.Deducted;
        if (penalty <= 0) return;

        h.Deducted += penalty;
        Deduct(penalty);
        if (showDebugLogs)
        {
            Debug.Log($"<color=red>[GNN] TEHLİKELİ ALAN ({HazardName(hazard)}, p={w:F2}, maruziyet={h.Exposure:F1} s, " +
                      $"KKD {(PpeComplete() ? "tam" : "eksik")}): -{penalty} (bu tehlike toplam {h.Deducted}/{Mathf.RoundToInt(budget)}) → Puan: {CurrentScore()}</color>");
        }
    }

    private void EnterCaution(GnnPrediction p, GnnHazardType hazard)
    {
        Deduct(cautionPenalty);
        if (warningSound != null) warningSound.Play();
        if (showDebugLogs)
            Debug.Log($"<color=orange>[GNN] DİKKAT ALANI ({HazardName(hazard)}, risk={p.RiskScore:F2}): -{cautionPenalty} → Puan: {CurrentScore()}</color>");
    }

    private static void Deduct(int penalty)
    {
        if (penalty > 0 && ScoreManager.Instance != null) ScoreManager.Instance.PuanDus(penalty);
    }

    private static int CurrentScore() => ScoreManager.Instance != null ? ScoreManager.Instance.GetScore() : 0;

    /// <summary>Puanlama yalnızca sınav sahnesinde, ekipmanlar alındıktan sonra yapılır (WrongPlaceCollider ile aynı koşul).</summary>
    private static bool IsActive()
    {
        return ManageScene.Instance != null && ManageScene.Instance.isExamScene && EquipmentBoxColliderController.isSceneStarted;
    }

    private bool IsElectricLive() => electricZone == null || electricZone.activeInHierarchy;

    private static bool PpeComplete() => EquipmentController.AllEquipped;

    private float PpeMultiplier() => PpeComplete() ? 1f : missingPpeMultiplier;

    private float Budget(GnnHazardType hazard)
    {
        switch (hazard)
        {
            case GnnHazardType.Crane: return craneBudget;
            case GnnHazardType.Scaffold: return scaffoldBudget;
            case GnnHazardType.Electric: return electricBudget;
            default: return 0f;
        }
    }

    private static string HazardName(GnnHazardType hazard)
    {
        switch (hazard)
        {
            case GnnHazardType.Crane: return "vinç";
            case GnnHazardType.Scaffold: return "iskele";
            case GnnHazardType.Electric: return "elektrik";
            default: return "genel";
        }
    }

    private static GameObject FindHazardObject(string tag)
    {
        foreach (GnnHazardZone z in GnnGraphBuilder.FindHazardZones())
        {
            if (z.Box.gameObject.tag == tag) return z.Box.gameObject;
        }
        return null;
    }
}
