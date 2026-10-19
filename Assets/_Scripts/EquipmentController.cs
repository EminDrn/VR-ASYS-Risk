using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EquipmentController : MonoBehaviour
{
    [Header("Equipment Status")]
    public static bool helmetEquipped = false;
    public static bool leftGloveEquipped = false;
    public static bool rightGloveEquipped = false;
    public static bool bootsEquipped = false;
    public static bool vestEquipped = false;

    /// <summary>Kişisel koruyucu donanımın hepsi giyildi mi (GNN puanlayıcı ceza çarpanı için kullanır).</summary>
    public static bool AllEquipped => helmetEquipped && leftGloveEquipped && rightGloveEquipped && bootsEquipped && vestEquipped;

    [Header("Settings")]
    public string zoneTag;

    [Header("Ses (Sadece bir objeye at, diğerlerini boş bırak)")]
    public AudioClip successSound;

    private static AudioClip sharedSuccessSound;
    private static AudioSource sharedAudioSource;
    private static bool allEquippedTriggered = false; // çift tetiklenmeyi önler

    void Start()
    {
        // Sahne başında static değişkenleri sıfırla
        helmetEquipped = false;
        leftGloveEquipped = false;
        rightGloveEquipped = false;
        bootsEquipped = false;
        vestEquipped = false;
        allEquippedTriggered = false;

        if (successSound != null)
        {
            sharedSuccessSound = successSound;
            sharedAudioSource = gameObject.AddComponent<AudioSource>();
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(zoneTag))
        {
            Destroy(other.gameObject);
            SetEquipped(zoneTag);
            CheckAllEquipped();
        }
    }

    void SetEquipped(string tag)
    {
        switch (tag)
        {
            case "Helmet":     helmetEquipped = true; break;
            case "LeftGlove":  leftGloveEquipped = true; break;
            case "RightGlove": rightGloveEquipped = true; break;
            case "Boots":      bootsEquipped = true; break;
            case "Vest":       vestEquipped = true; break;
        }
    }

    void CheckAllEquipped()
    {
        if (allEquippedTriggered) return; // zaten tetiklendiyse bir daha girme

        if (helmetEquipped && leftGloveEquipped && rightGloveEquipped &&
            bootsEquipped && vestEquipped)
        {
            allEquippedTriggered = true; // bir daha tetiklenmesin

            if (ScoreManager.Instance != null)
                ScoreManager.Instance.PuanEkle(30);

            Debug.Log("Tüm ekipmanlar giyildi! Başarılı!");

            // Sahnede inaktif bile olsa TeleportController'ı bul ve aktifleştir
            TeleportController teleport = FindObjectOfType<TeleportController>(true);
            if (teleport != null)
            {
                teleport.TeleportEnabled();
                Debug.Log("Teleport başarıyla aktif edildi!");
            }
            else
            {
                Debug.LogWarning("[EquipmentController] TeleportController sahnede bulunamadı!");
            }

            bool isExam = (ManageScene.Instance != null && ManageScene.Instance.isExamScene);
            if (sharedAudioSource != null && sharedSuccessSound != null && !isExam)
            {
                sharedAudioSource.PlayOneShot(sharedSuccessSound);
            }
        }
    }
}