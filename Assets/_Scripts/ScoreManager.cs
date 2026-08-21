using UnityEngine;
using TMPro;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance { get; private set; }

    [SerializeField] private TextMeshProUGUI scoreText;
    [SerializeField] private int baslangicPuani = 75;
    [SerializeField] private int maksPuan = 100;
    [SerializeField] private int minPuan = 0;
    public GameObject scoreTextGameObject; 
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
        scoreText.text = "Puan : " + mevcutPuan;
    }
}