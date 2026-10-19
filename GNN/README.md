# GNN Tabanlı İnşaat Sahası Risk Puanlaması

Sınav sahnesinde kullanıcının durduğu noktanın **güvenli**, **dikkat** veya **tehlikeli** olduğunu
eğitilmiş bir **GraphSAGE** modeli belirler. `Vinc` / `İskele` / `Electiric` trigger kutuları artık
puanlamada kullanılmıyor, yalnızca eğitim etiketi olarak işe yarıyor (GNN puanlayıcı yoksa veya model
yüklenemezse eski kutu cezaları otomatik devreye girer).

## Akış

1. **Graf** (`Assets/_Scripts/GNN/GnnGraphBuilder.cs`): Saha zemini 0.5 m'lik hücrelere bölünür.
   Her yürünebilir ve boş zemin hücresi bir düğümdür. Arada engel olmayan 8 komşu hücre kenarla bağlanır.
   Alan, tehlike kutularının çevresi 25 m genişletilerek bulunur (`GnnRiskPredictor > Site Area` ile elle verilebilir).
2. **Düğüm özellikleri** (10 adet, yalnızca geometriden): vince / iskeleye / elektrik kaynağına uzaklık
   (obje adlarından: `crane`, `scaffold`, `generator`, `cable`, `switch` …), baş üstü açıklık, çevre açıklığı,
   açık yön oranı, yakındaki en yüksek yapı, düşme kenarı, yüksekte duran serbest eşya.
3. **Etiket**: Kutunun içi `Danger`, kutuya 2.5 m'den yakın `Caution`, kalan saha `Safe`.
4. **Eğitim** (`GNN/train_gnn.py`): 2 katmanlı GraphSAGE (mean aggregator) + softmax.
   Veri 2 m'lik uzamsal bloklara göre bölünür, sınıf dengesizliği için ağırlıklı cross-entropy kullanılır.
5. **Çalışma zamanı**: Saha sabit olduğu için graf Editor'de bir kez çıkarılıp
   `Assets/Resources/GNN/gnn_graph_<sahne>.json` olarak kaydedilir. Oyunda sahne taranmaz;
   `GnnRiskPredictor` modeli sahne açılınca **bir kez** çalıştırır. `GnnRiskScorer` her 0.5 s'de
   oyuncunun (ana kamera) düğümünü sorgular:
   - Her tehlikenin şiddetine göre bir puan bütçesi vardır (Fine-Kinney şiddet ölçeği):
     elektrik 40, vinç 40 (öldürücü), iskele 30 (kalıcı hasar).
   - Bir tehlikenin toplam cezası:
     `bütçe × [0.4 × w_giriş + 0.6 × (1 − e^(−E / 5 s))] × KKD`
     - `w_giriş`: ilk girişteki GNN tehlike olasılığı (girişte bütçenin en fazla %40'ı hemen kesilir)
     - `E`: bölgede geçen, GNN tehlike olasılığıyla ağırlıklandırılmış süre
     - `KKD`: ekipman eksikse 1.5, tamsa 1
   - Ceza bütçeyi aşamaz; çıkıp girmek giriş payını tekrar kesmez, süre birikmeye devam eder.
   - Dikkat bölgesi varsayılan olarak yalnızca uyarıdır (`Caution Penalty` = 0, `Warning Sound` atanırsa ses çalar).
   - Şalterler kapatılınca (Electiric kutusu kapanınca) elektrik bölgesi tehlike sayılmaz.
6. **Sonuç**: Başlangıç 70, ekipmanın hepsi giyilince +30. Sınav bitince puanın altında standart risk etiketi gösterilir:
   75+ Güvenli, 50–74 Orta Riskli, 25–49 Riskli, 25 altı Yüksek Riskli.

   | Senaryo | Puan | Etiket |
   |---|---|---|
   | Ekipman tam, hata yok | 100 | Güvenli |
   | Ekipman tam, iskeleye 1 sn değdi | 85 | Güvenli |
   | Ekipman tam, iskelede uzun kaldı | 70 | Orta Riskli |
   | Ekipman tam, elektriğe 1 sn değdi | 80 | Güvenli |
   | Ekipman tam, elektrikte 10 sn | 63 | Orta Riskli |
   | Ekipman tam, vinç + iskelede uzun | 30 | Riskli |
   | Ekipman tam, vinç + elektrikte uzun | 20 | Yüksek Riskli |
   | Ekipmansız, tehlikeye girmedi | 70 | Orta Riskli |
   | Ekipmansız, iskeleye 1 sn değdi | 47 | Riskli |
   | Ekipmansız, elektrikte uzun kaldı | 10 | Yüksek Riskli |

   (Model tam emin varsayımıyla; tehlike bölgesinin kenarında cezalar daha düşük çıkar.)

## Kurulum

```bash
# 1) Unity: VR-ASYS-Risk-Sınav sahnesini aç
#    Hierarchy > Create Empty > adı "GNN" > Add Component: GnnRiskPredictor ve GnnRiskScorer > sahneyi kaydet
#    Tools > GNN > Eğitim Verisini ve Grafı Dışa Aktar (Açık Sahne)
#      → GNN/data/VR-ASYS-Risk-Sinav_graph_{0..3}.json (eğitim)
#      → Assets/Resources/GNN/gnn_graph_VR-ASYS-Risk-Sinav.json (oyun)

# 2) Eğit
python3 -m venv GNN/.venv && source GNN/.venv/bin/activate
pip install -r GNN/requirements.txt
python3 GNN/train_gnn.py            # --onnx ile ONNX da üretir

# 3) Assets/Resources/GNN/gnn_weights.json oluşur → Unity otomatik yükler
```

Görsel kontrol için sahnedeki `GnnRiskPredictor` bileşenine sağ tık:
- **Grafı kur ve etiketleri göster**: eğitim etiketleri (yeşil / sarı / kırmızı)
- **Kayıtlı grafla modeli çalıştır**: model tahmini

Bu görüntüleri görmek için objenin seçili olması gerekir (gizmo).

Dışa aktarım penceresi her tehlike türü için isimden kaç kaynak parçası bulunduğunu yazar. Biri 0 ise
`GnnRiskPredictor > Graph Settings` içindeki anahtar kelimeleri sahnedeki obje adlarına göre düzenleyin.

Metrikler `GNN/report/metrics.json` dosyasına yazılır:
- 5-katlı uzamsal çapraz doğrulama (ortalama ± std)
- Sınıf bazlı P/R/F1 ve karışıklık matrisi
- 1-komşu toleranslı metrikler (etiket kutularının kenar hatalarını ayırmak için)

## Bilinen sınırlama

Sahnede yalnızca 3 etiketli tehlike kutusu var, ama vinç ve iskele parçaları sahanın başka yerlerinde de
bulunuyor. Model etiketsiz iskele / vinç çevrelerini de riskli bulabilir: bu, metriklerde "hata" olarak
görünür ama oyunda gerçek bir riski işaretliyor olabilir. Gizmo ile haritayı kontrol edin; istenmeyen
bölgeler için ya etiket kutusu ekleyin ya da anahtar kelimeleri daraltın.

> Grid ayarları (`GnnGraphSettings`) veya özellik kodu değişirse veriyi yeniden dışa aktarıp
> modeli yeniden eğitin. Model, özellik sırası uyuşmazsa yüklenmeyi reddeder.
