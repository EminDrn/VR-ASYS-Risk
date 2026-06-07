
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WrongPlaceCollider : MonoBehaviour
{
    public AudioSource wrongPlaceSound;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player") && EquipmentBoxColliderController.isSceneStarted == true)
        {
           
            wrongPlaceSound.Play();
        }
    }
}
