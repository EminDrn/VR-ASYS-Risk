using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[System.Serializable]
public class QuestionData
{
    public string questionText;
    public string[] options = new string[4];
    public int correctAnswerIndex;
}

public class QuizManager : MonoBehaviour
{
    [Header("UI Elemanları")]
    public TMP_Text questionTextUI;     // Soru Metni
    public Toggle[] optionToggles;      // 4 Adet Toggle Butonu
    public TMP_Text[] optionTexts;      // 4 Adet Buton Metni

    [Header("Cevap Materyalleri")]
    public Material correctMaterial;    // Doğru cevap materyali (yeşil)
    public Material wrongMaterial;      // Yanlış cevap materyali (kırmızı)

    [Header("CSV Dosyası")]
    public TextAsset csvFile;

    private List<QuestionData> questionList = new List<QuestionData>();
    private int currentQuestionIndex = 0;
    private int score = 0;
    private bool isAnswered = false;

    private Material[] originalMaterials;
    private Material[] forcedMaterial;

    // YENİ: Dinamik oluşturulacak çıkış butonu referansı
    private GameObject exitButtonObject;

    void Awake()
    {
        if (optionToggles != null)
        {
            foreach (var toggle in optionToggles)
            {
                if (toggle != null)
                {
                    toggle.SetIsOnWithoutNotify(false);
                }
            }
        }
    }

    void Start()
    {
        LoadQuestionsFromCSV();
        CacheOriginalMaterials();

        forcedMaterial = new Material[optionToggles.Length];

        DisplayQuestion();
    }

    void LateUpdate()
    {
        if (isAnswered)
        {
            for (int i = 0; i < optionToggles.Length; i++)
            {
                if (forcedMaterial[i] != null)
                {
                    Transform bg = optionToggles[i].transform.Find("Content/Background");
                    if (bg != null)
                    {
                        Image img = bg.GetComponent<Image>();
                        if (img != null && img.material != forcedMaterial[i])
                        {
                            img.material = forcedMaterial[i];
                        }
                    }
                }
            }
        }
    }

    void Update()
    {
        if (!isAnswered)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
            {
                SelectOptionWithKeyboard(0);
            }
            else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
            {
                SelectOptionWithKeyboard(1);
            }
            else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            {
                SelectOptionWithKeyboard(2);
            }
            else if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
            {
                SelectOptionWithKeyboard(3);
            }
        }
    }

    void SelectOptionWithKeyboard(int index)
    {
        if (index < optionToggles.Length && optionToggles[index] != null)
        {
            optionToggles[index].isOn = true;
            OnOptionSelected(index);
        }
    }

    public void LoadQuestionsFromCSV()
    {
        if (csvFile == null)
        {
            Debug.LogError("QuizManager: CSV dosyası atanmadı!");
            return;
        }

        questionList.Clear();

        string fullText = csvFile.text.Replace("\r", "");
        string[] rawTokens = fullText.Split(new char[] { ';', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);

        List<string> cleanTokens = new List<string>();
        foreach (string token in rawTokens)
        {
            string t = token.Trim().Replace("\"", "");
            if (!string.IsNullOrEmpty(t)) cleanTokens.Add(t);
        }

        List<string> filteredTokens = new List<string>();
        foreach (string t in cleanTokens)
        {
            if (t.Contains("SoruText") || t.Contains("SecenekA") || t.Contains("SecenekB") ||
                t.Contains("SecenekC") || t.Contains("SecenekD") || t.Contains("DogruIndex"))
            {
                continue;
            }
            filteredTokens.Add(t);
        }

        for (int i = 0; i + 5 < filteredTokens.Count; i += 6)
        {
            try
            {
                QuestionData q = new QuestionData();
                q.questionText = filteredTokens[i];
                q.options[0] = filteredTokens[i + 1];
                q.options[1] = filteredTokens[i + 2];
                q.options[2] = filteredTokens[i + 3];
                q.options[3] = filteredTokens[i + 4];

                string rawIndexStr = filteredTokens[i + 5];
                string digitOnly = "";
                foreach (char c in rawIndexStr)
                {
                    if (char.IsDigit(c))
                    {
                        digitOnly += c;
                        break;
                    }
                }

                if (int.TryParse(digitOnly, out int indexVal))
                {
                    q.correctAnswerIndex = indexVal;
                    questionList.Add(q);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Soru dönüştürme hatası: {e.Message}");
            }
        }
    }

    void CacheOriginalMaterials()
    {
        originalMaterials = new Material[optionToggles.Length];
        for (int i = 0; i < optionToggles.Length; i++)
        {
            Transform bg = optionToggles[i].transform.Find("Content/Background");
            if (bg != null)
            {
                Image img = bg.GetComponent<Image>();
                if (img != null)
                {
                    originalMaterials[i] = img.material;
                }
            }
        }
    }

    void DisplayQuestion()
    {
        if (currentQuestionIndex >= questionList.Count)
        {
            questionTextUI.text = $"Test Bitti!\nToplam Skor: {score} / {questionList.Count}";
            foreach (var toggle in optionToggles) toggle.gameObject.SetActive(false);

            // YENİ: Sınav bittiğinde çıkış butonunu oluştur
            CreateExitButton();
            return;
        }

        isAnswered = false;
        ResetButtonMaterials();

        QuestionData q = questionList[currentQuestionIndex];
        questionTextUI.text = q.questionText;

        for (int i = 0; i < optionToggles.Length; i++)
        {
            optionTexts[i].text = q.options[i];
            optionToggles[i].onValueChanged.RemoveAllListeners();

            optionToggles[i].group = null;
            optionToggles[i].isOn = false;
            optionToggles[i].group = optionToggles[i].GetComponentInParent<ToggleGroup>();

            optionToggles[i].interactable = true;

            int index = i;
            optionToggles[i].onValueChanged.AddListener((isOn) => {
                if (isOn && !isAnswered) OnOptionSelected(index);
            });
        }
    }

    void OnOptionSelected(int selectedIndex)
    {
        if (isAnswered) return;
        isAnswered = true;

        foreach (var toggle in optionToggles)
        {
            toggle.interactable = false;
        }

        int correctIndex = questionList[currentQuestionIndex].correctAnswerIndex;

        if (selectedIndex == correctIndex)
        {
            forcedMaterial[selectedIndex] = correctMaterial;
            SetImageMaterial(optionToggles[selectedIndex], correctMaterial);
            score++;
        }
        else
        {
            forcedMaterial[selectedIndex] = wrongMaterial;
            forcedMaterial[correctIndex] = correctMaterial;
            SetImageMaterial(optionToggles[selectedIndex], wrongMaterial);
            SetImageMaterial(optionToggles[correctIndex], correctMaterial);
        }

        StartCoroutine(NextQuestionRoutine());
    }

    void SetImageMaterial(Toggle toggle, Material targetMaterial)
    {
        Transform bgTransform = toggle.transform.Find("Content/Background");
        if (bgTransform != null)
        {
            Image bgImage = bgTransform.GetComponent<Image>();
            if (bgImage != null)
            {
                bgImage.material = targetMaterial;
                bgImage.color = Color.white; 
            }
        }
    }

    void ResetButtonMaterials()
    {
        for (int i = 0; i < optionToggles.Length; i++)
        {
            forcedMaterial[i] = null;

            Transform bg = optionToggles[i].transform.Find("Content/Background");
            if (bg != null)
            {
                Image bgImage = bg.GetComponent<Image>();
                if (bgImage != null)
                {
                    if (originalMaterials[i] != null)
                    {
                        bgImage.material = originalMaterials[i];
                    }
                    bgImage.color = new Color(1f, 1f, 1f, 1f);
                }
            }
        }
    }

    IEnumerator NextQuestionRoutine()
    {
        yield return new WaitForSeconds(3.0f);
        currentQuestionIndex++;
        DisplayQuestion();
    }

    // YENİ: Uygulamadan Çıkış Butonunu dinamik oluşturan metod
    private void CreateExitButton()
    {
        if (exitButtonObject != null) return; // Zaten oluşturulduysa tekrar oluşturma

        // 1. Yeni bir GameObject (Buton) oluştur
        exitButtonObject = new GameObject("ExitButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        
        // Butonu QuestionText'in parent'ı olan Canvas/Panel altına yerleştir
        exitButtonObject.transform.SetParent(questionTextUI.transform.parent, false);

        // 2. Buton boyutlandırması ve Konumlandırması (Soru metninin altına getirir)
        RectTransform btnRect = exitButtonObject.GetComponent<RectTransform>();
        btnRect.sizeDelta = new Vector2(250f, 60f); // Genişlik: 250, Yükseklik: 60
        
        // Konum: Soru metninin hemen altında bulunması için Y koordinatında offset
        btnRect.anchoredPosition = questionTextUI.rectTransform.anchoredPosition + new Vector2(0, -120f);

        // 3. Buton arka plan rengi ve tıklama olayı
        Image btnImage = exitButtonObject.GetComponent<Image>();
        btnImage.color = new Color(0.8f, 0.2f, 0.2f); // Kırmızımsı buton rengi

        Button btn = exitButtonObject.GetComponent<Button>();
        btn.onClick.AddListener(QuitApp);

        // 4. Buton üzerindeki metni (TextMeshPro) oluştur
        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(exitButtonObject.transform, false);

        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.sizeDelta = Vector2.zero;

        TMP_Text tmpText = textObject.GetComponent<TextMeshProUGUI>();
        tmpText.text = "Uygulamadan Çık";
        tmpText.fontSize = 24;
        tmpText.alignment = TextAlignmentOptions.Center;
        tmpText.color = Color.white;
    }

    // YENİ: Uygulamadan Çıkış Fonksiyonu
    public void QuitApp()
    {
        Debug.Log("Uygulamadan çıkılıyor...");
        
        #if UNITY_EDITOR
            // Unity Editor içerisinde çalışırken Play modunu durdurur
            UnityEditor.EditorApplication.isPlaying = false;
        #else
            // Build alınmış uygulamada oyunu kapatır
            Application.Quit();
        #endif
    }
}