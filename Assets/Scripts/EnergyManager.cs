using System;
using UnityEngine;

public class EnergyManager : MonoBehaviour
{
    public SwitchState[] switches;
   public GameObject electricEffect;
    public AudioSource audioSource;
    [SerializeField] private GameObject colliderobject;
    public GameObject secondUI;
    public AudioSource electiricAudio;
    

    private bool wasAllOff = false; // önceki durumu takip et

    private void Start()
    {
        secondUI.SetActive(false);
    }

    void Update()
    {
        bool allOff = true;

        foreach (var sw in switches)
        {
            if (sw.isOn)
            {
                allOff = false;
                break;
            }
        }

        electricEffect.SetActive(!allOff);

        // Sadece durum değiştiğinde çal
        if (allOff && !wasAllOff)
        {
            colliderobject.SetActive(false);
            secondUI.SetActive(true);
            if (ManageScene.Instance.isExamScene == false)
            {
                audioSource.Play();

            }
            electiricAudio.Stop();
            if (ManageScene.Instance.isExamScene == true)
            {
                ScoreManager.Instance.GetScoreText();
            }
        }

        wasAllOff = allOff;
    }
}