using UnityEngine;
using TMPro;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance { get; private set; }

    [SerializeField] private TextMeshProUGUI scoreText;
    [SerializeField] private int baslangicPuani = 70;
    [SerializeField] private int maksPuan = 100;
    [SerializeField] private int minPuan = 0;
    public GameObject scoreTextGameObject;

    [Header("Risk Etiketi (sınav sonunda puana göre gösterilir)")]
    [SerializeField] private int guvenliEsigi = 75;
    [SerializeField] private int ortaRiskEsigi = 50;
    [SerializeField] private int riskliEsigi = 25;

    private int mevcutPuan;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        mevcutPuan = Mathf.Clamp(baslangicPuani, minPuan, maksPuan);
        PuaniGuncelle();
    }

    public void PuanEkle(int miktar)
    {
        mevcutPuan = Mathf.Clamp(mevcutPuan + miktar, minPuan, maksPuan);
        PuaniGuncelle();
    }

    public void PuanDus(int miktar)
    {
        mevcutPuan = Mathf.Clamp(mevcutPuan - miktar, minPuan, maksPuan);
        PuaniGuncelle();
    }

    public void PuaniSifirla()
    {
        mevcutPuan = minPuan;
        PuaniGuncelle();
    }

    public int GetScore()
    {
        return mevcutPuan;
    }

    public bool MaksPuanaMiUlasti()
    {
        return mevcutPuan >= maksPuan;
    }

    public bool MinPuanaMiUlasti()
    {
        return mevcutPuan <= minPuan;
    }

    private void PuaniGuncelle()
    {
        if (scoreText != null)
            scoreText.text = "Puan : " + mevcutPuan;
    }
    
    public void GetScoreText()
    {
        scoreTextGameObject.SetActive(true);
        scoreText.text = "Puan : " + mevcutPuan + $"\n<size=75%><color={GetRiskColor()}>{GetRiskLabel()}</color></size>";
    }

    /// <summary>Puana göre risk etiketi: yüksek puan = doğru davranış = düşük risk.</summary>
    public string GetRiskLabel()
    {
        if (mevcutPuan >= guvenliEsigi) return "Güvenli";
        if (mevcutPuan >= ortaRiskEsigi) return "Orta Riskli";
        if (mevcutPuan >= riskliEsigi) return "Riskli";
        return "Yüksek Riskli";
    }

    private string GetRiskColor()
    {
        if (mevcutPuan >= guvenliEsigi) return "#3CC864";
        if (mevcutPuan >= ortaRiskEsigi) return "#F0B43C";
        if (mevcutPuan >= riskliEsigi) return "#F07832";
        return "#E04646";
    }
}