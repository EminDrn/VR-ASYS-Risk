using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EquipmentBoxColliderController : MonoBehaviour
{
    public static bool isSceneStarted = false;
    public BoxCollider vest;
    public BoxCollider boots;
    public BoxCollider helmet;
    public BoxCollider leftglove;
    public BoxCollider rightglove;

    private void Start()
    {
        vest.enabled = false;
        boots.enabled = false;
        helmet.enabled = false;
        leftglove.enabled = false;
        rightglove.enabled = false;
    }

    public void SetColliderEnabled()
    {
        isSceneStarted = true;
        vest.enabled = true;
        boots.enabled = true;
        helmet.enabled = true;
        leftglove.enabled = true;
        rightglove.enabled = true;
    }
}
