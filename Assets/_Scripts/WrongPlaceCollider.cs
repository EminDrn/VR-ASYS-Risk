
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WrongPlaceCollider : MonoBehaviour
{
    public AudioSource wrongPlaceSound;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player") && EquipmentBoxColliderController.isSceneStarted == true && ManageScene.Instance.isExamScene == false)
        {
            if (gameObject.CompareTag("Vinc"))
            {
                ScoreManager.Instance.PuanDus(15);
            }
            if (gameObject.CompareTag("İskele"))
            {
                ScoreManager.Instance.PuanDus(15);
            }
            if (gameObject.CompareTag("Electiric"))
            {
                ScoreManager.Instance.PuanDus(25);
            }
            wrongPlaceSound.Play();
        }
    }
}
