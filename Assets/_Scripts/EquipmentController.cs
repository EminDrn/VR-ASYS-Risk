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

    [Header("Settings")]
    public string zoneTag;

    [Header("Ses (Sadece bir objeye at, diğerlerini boş bırak)")]
    public AudioClip successSound;

    // Ses için static referans — ilk atanan ses tüm script'lerde geçerli olur
    private static AudioClip sharedSuccessSound;
    private static AudioSource sharedAudioSource;

    void Start()
    {
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
        if (helmetEquipped && leftGloveEquipped && rightGloveEquipped &&
            bootsEquipped && vestEquipped)
        {
            Debug.Log("Tüm ekipmanlar giyildi! Başarılı!");

            if (sharedAudioSource != null && sharedSuccessSound != null)
                sharedAudioSource.PlayOneShot(sharedSuccessSound);
        }
    }
}