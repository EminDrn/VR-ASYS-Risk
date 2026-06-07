using UnityEngine;
using UnityEngine.SceneManagement;

public class ManageScene : MonoBehaviour
{
    public void YenidenBaslat()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public void SonrakiSahneGec()
    {
        int sonraki = SceneManager.GetActiveScene().buildIndex + 1;

        if (sonraki >= SceneManager.sceneCountInBuildSettings)
        {
            Debug.LogWarning("Bu son sahne! Geçilecek sahne yok.");
            return;
        }

        SceneManager.LoadScene(sonraki);
    }

    public void UygulamadanCik()
    {
        Debug.Log("Uygulama kapatılıyor...");
        Application.Quit();
    }
}