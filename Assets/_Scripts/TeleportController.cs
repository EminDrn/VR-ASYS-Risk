using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TeleportController : MonoBehaviour
{
    public GameObject teleportObject;

    private void Start()
    {
        teleportObject.SetActive(false);
    }

    public void TeleportEnabled()
    {
        teleportObject.SetActive(true);
    }
}
