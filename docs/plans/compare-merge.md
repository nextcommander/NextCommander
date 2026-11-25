# İçerik karşılaştırma ve birleştirme planı

## Bağlam

Bugün iki karşılaştırma aracı var.

**Dosya karşılaştırma penceresi** (`src/NextCommander.App/Views/Tools/CompareWindow.cs`, 1279 satır, tek dosya, `nc_CompareFilesByContent`):
- Yan yana ya da satır içi görünüm, karakter düzeyinde satır içi farklar.
- Eşzamanlı kaydırma, F2 / Shift+F2 / j / k ile farklar arasında gezinme, genel bakış şeridi.
- "Büyük/küçük harfi yok say" ve "boşlukları yok say" seçenekleri.
- İkili dosyalar için bayt bayt fark listesi.
- Fark motoru: `src/NextCommander.Core/Compare/TextDiff.cs` (Myers O(ND), 10.000 düzenlemede pes ediyor; değişen satırları yalnızca sırasına göre eşliyor).

**Klasör karşılaştırma:**
- `nc_CompareDirs`: yalnızca tek klasör, ad / tarih / boyuta göre panel işaretleme.
- Synchronize directories: `Views/Tools/SyncDirsView.cs` + `Core/Compare/DirectoryComparer.cs`; alt klasörlere iner, içerik seçeneği ve eylemleri var, Enter ile dosya çiftini karşılaştırma penceresinde açıyor.

**Eksik olanlar:**
- **Düzenleme:** blokları bir taraftan diğerine kopyalama (birleştirme), kaydetme, geri alma, fark görünümünün içinde düzenleme yok.
- **Seçenekler:** boş satırları ve satır sonlarını yok sayma, satır filtreleri (regex), taşınmış blok tespiti, kelime düzeyi fark yok.
- **Pencere içi:** dosya seçici, yer değiştirme, yeniden yükleme, bul / satıra git, kodlama seçimi yok.
- **Görünümler:** 3 yollu karşılaştırma ve birleştirme, patch üretme / uygulama, HTML rapor yok.
- **Dosya türleri:** hex yan yana görünüm, resim karşılaştırma, Office / PDF dosyalarını metin olarak karşılaştırma yok.
- **Klasör karşılaştırmanın kendi penceresi yok:** "klasör sonuçları → dosya farkı → geri dön" akışı eksik.
- **Ayarlar:** karşılaştırmaya özel ayar yok (yok sayma seçenekleri kaydedilmiyor; renk, yazı tipi, dış araç yok).

Amaç, aşağıdaki aşamalarla bu açıkları kapatmak. Her aşama tek başına yayımlanabilir; önerilen sıra önemden aşağıya doğru.

## Aşama 1: Fark motoru (Core) — her şeyin temeli

Dosya: `src/NextCommander.Core/Compare/TextDiff.cs`; ayrıca `DiffDocument` / `DiffBlock` modeli.

- **Blok modeli:** satır listesinin yanında fark blokları gelir: sol ve sağ aralık ile türü. Birleştirme, gezinme ve rapor bunları kullanır.
- **Değişen satırların eşlenmesi:** blok içindeki silinen / eklenen satırlar benzerliğe göre eşlenir (satır içi farkın oranı). Bugün yalnızca sırasına göre eşleniyor.
- **Seçenekler** (`DiffOptions`):
  - boş satırları yok say;
  - satır sonlarını (CRLF / LF) yok say (kapalıyken farklı satır sonu bir fark sayılır);
  - kullanıcı regex filtreleri: eşleşen satırlar ya da satır parçaları yok sayılır (satır filtreleri ve değiştirme filtreleri).
- **Taşınmış bloklar:** bir tarafta silinip öbür tarafta eklenen aynı satırlar "taşındı" olarak işaretlenir.
- **Kelime düzeyi satır içi fark:** önce kelime sınırları, sonra karakter; ayarlanabilir.
- **Büyük dosyalar:** Myers'ın D² belleği yerine doğrusal bellek (orta yılan / böl ve yönet). Böylece 10.000 düzenleme sınırı kalkar. İsteğe bağlı histogram / patience algoritması.
- **3 yollu fark:** taban + iki taraf; çakışma blokları (Aşama 6 için).
- **Çıktılar:** unified diff (patch) üretme, ve bir patch'i dosyaya uygulama.
- **Testler:** `tests/NextCommander.Core.Tests/Phase5CoreTests.cs` genişletilir ya da yeni `TextDiffTests` eklenir (seçenekler, taşınma, büyük girdi süresi, patch gidiş-dönüş).

## Aşama 2: Dosya karşılaştırma penceresinde düzenleme ve birleştirme

`CompareWindow.cs` parçalara bölünür: `CompareWindow.Merge.cs`, `.Toolbar.cs`, `.Panes.cs`.

- **Düzenleme:** yan yana iki düzenlenebilir editör; düzenlerken farklar arka planda yeniden hesaplanır (gecikmeli, yaklaşık 300 ms). Bugünkü ayrı "Edit" görünümü kalkar.
- **Blok kopyalama:**
  - Alt+→ / Alt+← ("Copy to Right / Left"); her bloğun kenarında ok düğmeleri.
  - "Tümünü sağa / sola kopyala".
  - Birden çok fark seçilip kopyalanabilir.
- **Geri al / yinele:** iki taraf ortak bir geçmiş kullanır, birleştirme adımları dahil.
- **Kaydetme:**
  - Ctrl+S ile etkin taraf; "Tümünü kaydet".
  - Kodlama, satır sonu ve BOM korunur; yedek dosya ayarı.
  - Kaydedilmemiş değişiklikler için kapatırken sorulur; başlıkta `*` görünür.
  - Uzak dosyalar ve arşiv içi dosyalar provider üzerinden yazılır, yazılamıyorsa "salt okunur" gösterilir.
- **Gezinme:** ilk / önceki / sonraki / son fark; Alt+↑ / Alt+↓ ve alışılmış karşılaştırma tuşları; genel bakış şeridinde görünen alanın işareti.
- **Bul ve satıra git:** Ctrl+F (iki tarafta), Ctrl+G.
- **Dosyalar:**
  - üstte her taraf için yol çubuğu ve gözat düğmesi;
  - yer değiştirme (taraflar);
  - dosya diskte değişince yeniden yükleme sorusu (FileSystemWatcher);
  - taraf başına kodlama ve satır sonu seçici.
- **Sözdizimi renklendirmesi** her tarafın kendi uzantısına göre yapılır.
- **Durum çubuğu:** fark sayısı, satır / sütun, kodlama, satır sonu, değiştirildi bilgisi.
- **Tutarlılık:** bütün işlemler `nc_` komutu olur; pencerenin kendi menüsü ve araç çubuğu ikonlu olur (`MenuIcons` içinde `compare.*` anahtarları).

## Aşama 3: Karşılaştırma ayarları

- `AppSettings` içinde `CompareOptions`:
  - yok sayma seçenekleri ve filtre listesi;
  - kelime ya da karakter düzeyi;
  - taşınmış blokları göster;
  - yazı tipi (varsayılan Lister'ınki);
  - fark renkleri (tema anahtarları);
  - yedek oluşturma;
  - isteğe bağlı dış karşılaştırma programı (`%1 %2` parametreleriyle).
- Ayarlar penceresinde yeni sayfa "Tools/Compare". Satır filtreleri `ListEditor` ile düzenlenir; JSON yazdırılmaz.
- Penceredeki seçenek düğmeleri ayarları günceller ve kalıcı olur.

## Aşama 4: Klasör karşılaştırma penceresi

- **Ortak motor:** Synchronize directories ile aynı motor (`DirectoryComparer`).
- **Karşılaştırma yöntemi** seçilebilir:
  - boyut / tarih;
  - tam içerik;
  - metin dosyalarında Aşama 1'in yok sayma seçenekleriyle içerik (yalnızca satır sonu veya boşluk farkı olanlar "aynı" sayılır).
- **Liste:** ağaç ya da düz görünüm, durum simgeleri, filtreler.
- **Gezinme:**
  - Enter dosya karşılaştırma sekmesine gider, geri dönülebilir;
  - alt klasöre girme;
  - karşılaştırma penceresinde "sonraki farklı dosya" (Ctrl+Alt+↓).
- **Eylemler:**
  - kopyala → / ←, sil;
  - kaydedildiğinde satır durumu yenilenir;
  - sonuç listesini HTML / CSV olarak dışa aktar.
- **Açılış yolları:**
  - iki panelin klasörlerinden `nc_CompareDirsContent` (yeni komut);
  - Shift+F2'nin hızlı işaretlemesi kalır, ayrıca "pencerede aç" eklenir.
- **Ön ayarlar:** adlandırılmış karşılaştırma ön ayarları (sol/sağ klasör + seçenekler), bir karşılaştırma projesi gibi.
- Karar (kullanıcı, 25 Kasım 2026): Synchronize directories ekranı klasör karşılaştırma penceresiyle **birleşir**; tek bir sayfa hem karşılaştırır hem eşitler.

## Aşama 5: Dışa aktarma ve patch

- **Patch üret:** fark → unified diff (`.patch`), seçili dosyalar ya da klasör sonucu için.
- **Patch uygula:** `nc_ApplyPatch`; önizleme karşılaştırma penceresinde, uyuşmayan parçalar raporlanır.
- **HTML rapor:** yan yana, renkli, tek dosya.

## Aşama 6: 3 yollu karşılaştırma ve birleştirme

- Üç bölme (sol / taban / sağ) ya da sonuç bölmesi; çakışma gezinmesi.
- Çakışmalarda "soldan al", "sağdan al", "ikisini de al".
- Git eklentisinin çakışma akışına bağlanır (`GitActions.Remote.cs`: çakışan dosyayı burada aç, sonucu kaydet, `git add`).

## Aşama 7: Özel dosya türleri

- **Hex karşılaştırma:** iki hex editör, eşzamanlı kaydırma, farklı baytlar renkli, sonraki / önceki fark. Bugünkü liste görünümünün yerini alır.
- **Resim karşılaştırma:** yan yana, fark katmanı ve eşik (`remaining-work.md:334` maddesi).
- **Belgeler:** Office / PDF metin olarak karşılaştırılır (`Core/Documents/OfficeHtml`, `PdfDocument.PageText` ile metin çıkarılır, salt okunur).
- **Arşivler ve uzak dosyalar:** okunduğu gibi yazılabilir de olmalı.

## Kullanılacak mevcut parçalar

- `TextDiff`, `DirectoryComparer`, `SyncJob`: `src/NextCommander.Core/Compare/`.
- `CompareWindow` içindeki `DiffPane`, `LineColors`, `LineMargin`, `OverviewRuler` (bölünüp yeniden kullanılır).
- `EncodingDetector`, `ProviderRegistry` (okuma ve yazma), AvaloniaEdit renklendirmesi.
- `ListEditor` (filtre listesi), `MenuIcons`, `build/add_l10n_keys.py` (altı dil).
- Workspace sayfası yapısı (`MainWindow.Workspace.cs`): klasör karşılaştırma bir sayfa olabilir.

## Doğrulama (her aşamada)

- **Core:** birim testleri (seçenekler, birleştirme sonrası metin, kaydedilen dosyanın kodlama / satır sonu korunması, patch gidiş-dönüş, büyük dosya süresi).
- **App:** headless testler (blok kopyalama + geri alma + kaydetme diske yazıyor; kaydedilmemiş değişiklik sorusu; klasör → dosya → geri akışı). `FM_SCREENSHOT_DIR` ile açık / koyu ekran görüntüleri.
- **Her zaman:** `MenuTests`, `UserFilesTests` (yeni metinler), `dotnet build` uyarısız.
- **Belgeler:** `docs/guide/tools.md` (karşılaştırma bölümleri) ve `docs/remaining-work.md` güncellenir.
- **Elle:** büyük bir kaynak dosyası, CRLF/LF farkı olan dosyalar, arşiv içindeki bir dosya, bir SFTP dosyası.

## Önerilen sıra

1. Aşama 1 + 2 (motor, düzenleme, birleştirme, kaydetme): en büyük kazanç.
2. Aşama 3 (ayarlar).
3. Aşama 4 (klasör penceresi).
4. Aşama 5 (patch / rapor).
5. Aşama 7 (hex / resim / belge).
6. Aşama 6 (3 yollu birleştirme, en büyük iş).
