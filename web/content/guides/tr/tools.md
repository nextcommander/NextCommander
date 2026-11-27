# Araçlar

[README'ye dön](../../README.md)

- [Lister (F3)](#lister-f3)
- [Hızlı görünüm (Ctrl+Q)](#quick-view-ctrlq)
- [Komut paleti (Ctrl+Shift+P)](#command-palette-ctrlshiftp)
- [Geri al (Ctrl+Z)](#undo-ctrlz)
- [Düzenleyici (F4)](#editor-f4)
- [Dosya bul (Alt+F7)](#find-files-altf7)
- [Çoklu yeniden adlandırma (Ctrl+M)](#multi-rename-tool-ctrlm)
- [Klasörleri eşitle](#synchronize-directories)
- [İçeriğe göre karşılaştır](#compare-by-content)
- [Yinelenen dosyalar](#duplicate-files)
- [Klasör boyutları](#folder-sizes)
- [Sağlama toplamları](#checksums)
- [Böl ve birleştir](#split-and-combine)
- [Diğer dosya araçları](#more-file-tools)
- [Dahili terminal](#built-in-terminal)
- [Dosya indeksi](#file-index)

## Lister (F3)

Lister, bir dosyayı değiştirmeden gösterir. Sayı tuşları görünümler arasında geçiş yapar:

| Tuş | Görünüm |
|---|---|
| 1 | Metin; kaynak kod için sözdizimi renkleri ve tıklanabilir bağlantılar |
| 2 | İkili (sabit genişlik) |
| 3 | Hex |
| 4 | Resim: pencereye sığdır veya yakınlaştır, L / R ile döndür, kamera RAW önizlemeleri |
| 5 | Belge önizlemesi: PDF sayfaları, Word / Excel / PowerPoint ve OpenDocument dosyaları, HTML, Markdown, RTF |
| 6 | Bir eklentinin eklediği görünüm (ör. bir fotoğrafın üst verisi: kamera, çekim tarihi, harita bağlantılı GPS konumu, IPTC / XMP açıklaması, tüm etiketler) |
| 7 | Ses ve video |

![Kaynak kodu gösteren Lister](../screenshots/lister.png)

- **Kodlama:** otomatik algılanır. A / S / U / 8 metni ANSI, DOS, UTF-16 veya UTF-8 olarak gösterir; sağ tık
  menüsü tüm kod sayfalarını listeler.
- **Büyük dosyalar** hemen açılır: yalnızca görünen kısım okunur.
- **Bul:** F7 arama çubuğunu açar (büyük/küçük harf, tam sözcük, düzenli ifade, hex); F3 / Shift+F3 sonraki ve
  önceki eşleşmeyi bulur.
- **N / P** panelin sonraki / önceki dosyasına geçer (seçili dosya varsa yalnızca seçili dosyalar arasında).
- **W** sözcük kaydırmayı açar ve kapatır; Ctrl+fare tekerleği yazı boyutunu değiştirir.
- **F4** dosyayı düzenler, **F2** yeniden yükler. Lister, dosya değiştiğinde kendiliğinden de yeniden yükler.
- HTML ve Markdown **önizlemeleri** internetten resim yüklemez; bağlantılar tarayıcınızda açılır.
- **Belgeler** önizlemede açılır (Hızlı görünüm panelinde de, Ctrl+Q):
  - **PDF:** sayfalar alt alta gösterilir; yalnızca görünüme yakın sayfalar çizilir, bu yüzden büyük PDF'ler hemen açılır.
    Ctrl+fare tekerleği (veya Ctrl + / Ctrl -) yakınlaştırır, Ctrl+0 genişliğe yeniden sığdırır; durum satırı sayfayı gösterir.
    **F7** sayfalarda metin arar (F3 / Shift+F3 sonraki / önceki eşleşme; eşleşme işaretlenir ve oraya kaydırılır);
    metni seçmek için fareyle sürükleyin, sayfalar arasında da olur (üstten veya alttan öteye sürüklemek kaydırır;
    Shift+tık seçimi genişletir), Ctrl+A sayfanın metnini seçer, Ctrl+C kopyalar (her sayfanın parçası ayrı bir satırda).
    Korumalı bir PDF parolasını sorar (yanlışsa yeniden sorar).
  - **Word, Excel, PowerPoint** (docx, xlsx, pptx ve makro / şablon türleri) ve **OpenDocument** (odt, ods,
    odp) dosyaları Office olmadan okunur: başlıklar, vurgular, listeler, tablolar ve resimlerle metin; bir çalışma
    kitabının görünür her sayfası sütun harfleri ve satır numaralarıyla bir tablo olarak (tarihler tarih olarak);
    slaytlar başlıkları ve metinleriyle.
    Bu, içeriğin önizlemesidir, sayfa düzeninin değil; uzun belgeler kesilir (sonda "(…)" görünür).
  - **Eski Office dosyaları** (Office 97-2003'ün doc, xls, ppt dosyaları): LibreOffice kuruluysa PDF'ye çevrilir
    (gerçek sayfa düzeni); kurulu değilse metinleri doğrudan okunur: belgenin başlıklı paragrafları, tabloları
    ve kalın / italik / altı çizili / üstü çizili sözcükleri, bir çalışma kitabının görünür sayfaları (metinler, sayılar,
    tarihler, formül sonuçları; kalın hücreler), slaytların başlıkları ve metinleri.
  - **Diğer belgeler** (Visio, Publisher, WordPerfect, Keynote ...) **LibreOffice** kuruluysa onunla PDF'ye
    çevrilir (bir kez: PDF geçici klasörde saklanır); kurulu değilse ikili olarak gösterilir.
  - *Yapılandırma > Düzenle ve görüntüle > PDF, Office, HTML, Markdown ve RTF dosyalarını önizlemeyle aç* kapalıysa bunlar
    metin / hex olarak açılır; 5 tuşu yine de önizlemeyi gösterir.
- **Ses ve video**, bilgisayarınızda kurulu VLC oynatıcıyla oynatılır (programa dahil değildir). Oynat ve duraklat,
  10 veya 60 saniye atla, hızı ve sesi değiştir, bölüm seç ve kaldığın yerden devam et.

![Markdown önizlemesi](../screenshots/markdown-preview.png)

![Koyu temada Lister](../screenshots/lister-dark.png)

## Hızlı görünüm (Ctrl+Q)

Hızlı görünüm, imlecin altındaki dosyayı diğer panelin yerinde gösterir ve imleci izler.
Tab görünüme geçirir ve geri getirir.

Başlığındaki ⇄ düğmesi onu taşır: diğer panelin yerine, dosya listesinin altına veya sağına. Dosya listesinin altında veya
sağında iki panel de kalır; Hızlı görünüm etkin paneldedir ve panel değiştirdiğinizde onunla birlikte gider, liste ile
görünüm arasındaki çizgi sürüklenebilir. Konum hatırlanır (*Yapılandırma > Seçenekler > Düzen/Görünüm > Lister*).

![Hızlı görünüm](../screenshots/quick-view.png)

![Dosya listesinin sağındaki Hızlı görünüm](../screenshots/quick-view-right.png)

## Komut paleti (Ctrl+Shift+P)

Ctrl+Shift+P (Komutlar > Komut paleti...) pencereyi karartır ve üstünde bir kutu açar. Birkaç sözcük yazın: tüm komutları
(menüsü ve kısayoluyla), makrolarınızı (adımlarıyla), Hotlist'i ve en çok kullanılan klasörleri, ayrıca kayıtlı
bağlantıları bulur. Her sözcüğün başlıkla, adla, menüyle veya türle eşleşmesi gerekir; sözcüğün başı en çok
puan alır, sonra bir parçası, sonra harflerinin sırayla geçmesi (`nmk`, *nc_MkDir* komutunu bulur); tam yazılmış bir kısayol (`f5`,
`ctrl+p`) kendi komutunu bulur. Büyük/küçük harf ve aksanlar önemli değildir; bulunan harfler vurgulanır.

- **Gruplar:** hiçbir şey yazılmamışsa en son kullandıklarınız, ardından birkaç komut, klasör ve bağlantı; sözcük
  yazılınca önce en iyi eşleşme, sonra her türün en iyisi ("Tümünü göster" hepsini listeler).
- **Kapsamlar:** Tümü, Komutlar, Klasörler, Bağlantılar; Tab bunlar arasında dolaşır, ya da `>` (komutlar), `/` (klasörler) veya
  `@` (bağlantılar) ile başlayın.
- **Bir yol** (`D:\Projects`, `\\nas\share`, `~/docs`) *Git* seçeneğini ve altındaki klasörleri sunar; **bir adres**
  (`sftp://host`) *Bağlan* (kaydedilmeden) veya *Yeni bağlantı olarak kaydet* seçeneğini sunar; geri kalan her şey dosya
  adı olarak aranabilir (*Adı ... olan dosyaları bul*).
- **Tuşlar:** ↑ / ↓ seçer, Enter çalıştırır; klasör için Ctrl+Enter yeni sekmede, Shift+Enter diğer panelde açar;
  bağlantı için Ctrl+E düzenler; komut için Ctrl+C adını kopyalar, Ctrl+K tuşunu değiştirmek üzere *Komutlar ve kısayollar*
  penceresini o komutta açar. Esc önce metni siler, sonra kapatır; karartılmış pencereye tıklamak da kapatır.

![Komut paleti](../screenshots/command-palette.png)

## Geri al (Ctrl+Z)

Ctrl+Z (Düzen > Geri al) son dosya işlemini geri alır; yeniden basarsanız bir öncekini geri alır (en fazla 30). Bir şey
yapmadan önce ne yapılacağını söyler (hangi kopyalar silinir, neyin nereye döneceği, hangi adların geri geleceği, geri
getirilemeyen dosyalar) ve sorar:

- **Kopyalama**: kopyalar silinir (yerel dosyalarda geri dönüşüm kutusuna), ardından kopyalamanın oluşturduğu klasörler. Kopyalamanın
  üzerine yazdığı bir dosya geri getirilemez; kopyası saklanır ve size bildirilir.
- **Taşıma**: her şey eski yerine döner (mümkün olan yerde adı geri değiştirilir, değilse geri kopyalanır).
- **Yeniden adlandırma** ve **Çoklu yeniden adlandırma**: eski adlar geri gelir.
- **Yeni klasör**: klasörler, hâlâ boş oldukları sürece silinir.
- **Geri dönüşüm kutusuna silme**: dosyalar oradan geri gelir (Windows ve Linux'ta; macOS'ta Çöp Sepeti'ni kullanın).

Kalıcı silme, Windows Gezgini ile kopyalama ve yönetici haklarıyla yeniden yapılan işlemler geri alınamaz. Bir metin kutusu
odaktayken (komut satırı, hızlı süzgeç) Ctrl+Z her zamanki gibi yazmayı geri alır. Açıklama düzenleme Ctrl+Z'den
Ctrl+Alt+Z'ye, klasör yolunu kopyalama Ctrl+Shift+P'den Ctrl+Alt+P'ye taşındı.

## Düzenleyici (F4)

F4 yerleşik metin düzenleyiciyi açar; Shift+F4 yeni bir dosya oluşturur.

![Düzenleyici](../screenshots/editor.png)

- Dosyanın kodlamasını ve satır sonlarını korur; ikisini de menüsünden değiştirebilirsiniz.
- Ctrl+S kaydeder, Ctrl+F bulur, Ctrl+H değiştirir, Ctrl+G bir satıra gider.
- *Görünüm > Yazı tipi...* düzenleyicinin yazı tipini ve boyutunu seçer (önce sabit genişlikli yazı tipleri, önizlemeyle); sonraki
  dosyalar için saklanır. Aynı ayar Yapılandırma penceresinde *Yazı tipi > Düzenleyici yazı tipi* olarak da vardır; boş bırakılırsa düzenleyici
  Lister'ın yazı tipini kullanır.
- Kaydedilmemiş değişiklikler varken kapatmadan önce sorar ve başka bir program dosyayı değiştirdiğinde uyarır.
- Bir karakter dosyanın kodlamasında saklanamıyorsa uyarır ve kaydetmez.
- Bunun yerine başka bir düzenleyici kullanabilirsiniz: [Yapılandırma penceresinin](configuration.md) *İşlem > Düzenle ve görüntüle* sayfasında,
  dosya türüne göre de ayarlayabilirsiniz (örneğin `*.docx` için bir kelime işlemci).
  İkili dosyalar harici düzenleyicinizle veya sistemin programıyla açılır.

## Dosya bul (Alt+F7)

![Dosya bul](../screenshots/find-files.png)

- **Aranacak:** `*.cs *.xaml` gibi bir ad deseni veya düzenli ifade. Klasörleri atlamak için `| obj\ bin\` ekleyin:
  `*.cs | obj\ bin\`.
- **Aranacak yer:** `;` ile ayrılmış bir veya daha fazla klasör ya da *Tüm sürücüler*. Derinliği sınırlayabilir ve
  dosya, klasör veya ikisini birden arayabilirsiniz.
- **İçerik:** dosyaların içindeki metni bulur; UTF-16 metinlerde ve Office, OpenDocument ve EPUB dosyalarının içinde de. Seçenekler:
  büyük/küçük harf duyarlı, tam sözcük, düzenli ifade, *İçermeyenler* ve *İkili dosyaları atla* (programlar,
  resimler ve arşivler okunmaz). Birkaç dosya aynı anda okunur. Her sonuç, ilk eşleşmesinin satırını numarasıyla
  gösterir; F3 Lister'ı o eşleşmede açar (orada F3 sonrakine gider).
- **Gelişmiş:** boyut (`10k`, `2M`), tarih (`2024-05-01` veya son yedi gün için `7d`), öznitelikler, gizli klasörleri atlama,
  yalnızca önceki sonuçlarda arama ve yinelenenleri bulma.
- **Sonuçlar:** her satırda türünün simgesi vardır (klasör, belge, resim, arşiv ...). F3 görüntüler, F4 düzenler,
  Enter dosyaya gider, Shift+Enter dosyayı kendi programıyla açar, Delete çöp kutusuna taşır (Shift+Delete
  kalıcı olarak siler), Ctrl+C tam yolu kopyalar. Ctrl ve Shift ile birden çok sonuç seçilebilir.
  - Bir sonuca **sağ tık**: aç, panelde ona git, sistem dosya yöneticisinde göster, görüntüle, düzenle,
    yolu veya adı kopyala, sil, yalnızca listeden kaldır ve Windows'ta özellikleri.
  - **Dışa aktar...** sonuçları yol listesi olarak (`.txt`, satır başına bir yol; *LOADLIST* onu bir panelde açar) ya da
    elektronik tablo için bir tablo olarak (`.csv`: yol, ad, klasör, tür, boyut, değiştirilme tarihi ve yinelenenler
    aranmışsa grup) kaydeder.
  - **Panele aktar** tüm sonuçları bir panelde gösterir; orada bunları normal dosyalar gibi kopyalayabilir, taşıyabilir veya silebilirsiniz.
- **Kayıtlı aramalar:** bir aramayı kaydedin ve daha sonra yeniden çalıştırın.
- [Dosya indeksi](#file-index) açıkken ad aramaları neredeyse anında yanıtlanır.

*Komutlar > Ayrı süreçte dosya bul...* (Alt+Shift+F7) siz çalışmaya devam ederken aramayı kendi penceresinde çalıştırır.

## Çoklu yeniden adlandırma (Ctrl+M)

Birçok dosyayı bir seferde yeniden adlandırır. Liste, siz yazarken eski ve yeni adları canlı gösterir.

![Çoklu yeniden adlandırma](../screenshots/multi-rename.png)

- **Ad maskesi** ve **Uzantı:** yeni adı yer tutuculardan oluşturun. `[N]` eski addır (parçaları da:
  `[N2-5]` 2'den 5'e kadar karakterler), `[E]` uzantı, `[P]` üst klasör, `[C]` sayaç, `[YMD]` ve `[hms]`
  tarih ve saat, `[S]` boyut. `[...]` düğmeleri bunları ekler.
- **Bul / Şununla değiştir:** adlardaki metni değiştirir; `|` ile ayrılmış birkaç terim, düzenli ifadeler,
  *Yalnızca ilki* ve *Uzantı dahil* seçenekleriyle.
- **Büyük/küçük harf:** küçük harf, BÜYÜK HARF, İlk harf büyük, Her Sözcük Büyük.
- **Sayaç:** başlangıç, adım ve basamak sayısı.
- Çakışacak, zaten var olan veya geçersiz adlar işaretlenir ve atlanır.
- Yeni addaki bir `/` dosyayı o alt klasöre taşır.
- **Geri al** son yeniden adlandırmayı geri alır. Ayarlar kaydedilip yüklenebilir.

## Klasörleri eşitle

*Araçlar > Klasörleri eşitle...* iki klasörü karşılaştırır ve eşit hâle getirir; klasör karşılaştırması da budur.
Panellerin yerinde, kendi başına bir
sayfa olarak açılır; dosyalar sekmesinin yanında bir **Eşitle** sekmesi bulunur: dosyalar sekmesi karşılaştırma açık kalırken sizi panellere geri götürür; sekmenin `×` düğmesi,
sağ üstteki `×` veya Esc onu kapatır.

![Klasörleri eşitle](../screenshots/synchronize.png)

- **Klasörler** üsttedir, her birinin dosya sayısı ve boyutuyla; klasör düğmesi birini seçer, ⇄ onları değiştirir.
  Karşılaştırma pencere açıldığında ve **Karşılaştır** (F5) ile ya da bir seçenek değiştiğinde yeniden çalışır.
- **Karşılaştırma** iki dosyayı neyin aynı yaptığını belirler: *Tarih ve boyut*, *Tarih ve boyut, sonra baytlar*, *İçerik
  (baytlar)* (her çift okunur; tarih yalnızca farklı olduklarında hangisinin daha yeni olduğunu söyler) veya *Metin (karşılaştırma
  ayarları)*: metin dosyaları *İçeriğe göre karşılaştır* işleminin kendi ayarlarıyla yaptığı gibi karşılaştırılır; böylece yalnızca satır
  sonları, boşluklar veya satır süzgeçlerinin dışarıda bıraktığı kısımlar bakımından farklı dosyalar aynı sayılır.
- **Seçenekler** çiplerdir: *Alt klasörler*, *Tarihi yok say*, *Tek yönlü (sol → sağ)* (yalnızca sağdaki dosyalar geri kopyalanmaz),
  *Gizlileri atla*, *Düz liste* (her dosya tüm yoluyla, klasörsüz) ve bir dosya **Maskesi** (`*.cs *.md`).
- **Ön ayarlar** iki klasörü ve seçenekleri bir adla saklar (*Ön ayar olarak kaydet...*) ve bir
  tıkla geri getirir; **Dışa aktar** gösterilen listeyi HTML raporu, CSV dosyası veya farklı dosyaların tek bir **yaması** olarak
  kaydeder (sol klasör eski durum, sağ klasör yeni durum; ikili dosyalar yalnızca adlandırılır).
- **Araçlar > Yama uygula...** bir yamayı uygular (.patch / .diff, git'inkiler de): imleç bir yama dosyasındayken
  diğer panelin klasörüne uygular, aksi hâlde etkin panelin klasörü için bir yama seçilir. Bir liste yamanın her dosyasını ve
  değişikliklerinin kaçının uyduğunu gösterir (dosya ilerlemiş olsa bile değişiklikler bulunur); Enter bir dosyanın
  sonucunu karşılaştırma penceresinde gösterir, *Yamayı uygula* uyanları her dosyanın kendi kodlaması ve satır sonlarıyla yazar ve
  uymayanları bildirir.
- **Süzgeçler** sayılarıyla birlikte *Farklı*, *Yalnızca sol*, *Yalnızca sağ* ve *Aynı* dosyaları gösterir veya gizler;
  **Ada göre süz** listeyi daraltır.
- **Liste bir ağaçtır**: klasörler açılır ve kapanır, ve kaç değişiklik içerdiklerini gösterir. Her satırda iki tarafın boyutu ve
  tarihi (yeni olan tarih işaretlenir), durum (yalnızca sol, yalnızca sağ, sol daha yeni, sağ daha yeni,
  farklı, aynı) ve **eylem** vardır: uyan sonraki eyleme geçmek için tıklayın (sağa kopyala, sola
  kopyala, atla, sil); bir klasörde tüm dosyalarının eylemini ayarlar. ← / → ve Delete imlecin altındaki
  satır için aynısını yapar; çift tık (veya Enter) bir dosyanın içeriğini karşılaştırma penceresinde karşılaştırır. Orada
  Ctrl+Alt+↓ / ↑ listenin sonraki / önceki farklı dosyasına gider ve bir dosyayı kaydetmek buradaki satırını günceller.
- **Onay kutuları** dosyaları seçer (bir klasör: tüm dosyaları, Ctrl+A: hepsi). Dosyalar seçiliyken bir çubuk hepsi için *Kopyala →*,
  *Kopyala ←*, *Atla*, *Solda sil*, *Sağda sil* ve *İçeriğe göre karşılaştır* seçeneklerini sunar.
- Aşağıda plan, her tarafa ne kadarının gittiğini ve ne kadarının silindiğini söyler. **Eşitle** önce bir özet gösterir
  (silinen dosyaları Geri Dönüşüm Kutusu'na gönderme seçeneğiyle), işlemleri çalıştırır ve yeniden karşılaştırır; ardından
  aynı hâle gelen dosyalar gösterilir.

## İçeriğe göre karşılaştır

*Dosya > İçeriğe göre karşılaştır...* iki dosyayı karşılaştırır: işaretli iki dosyayı veya iki paneldeki imlecin
altındaki dosyaları.

![İçeriğe göre karşılaştır](../screenshots/compare.png)

- Çubuk dosyayı, kaç fark olduğunu ve kaç satırın eklendiğini (+) ve kaldırıldığını (−) gösterir.
- **Bölünmüş** iki dosyayı yan yana gösterir (birlikte kayarlar); **Tek sütun** tek bir sütun gösterir, her farkın kaldırılan
  satırları eklenenlerin üstündedir. Sol dosya *Asıl*, sağ dosya *Güncel* olarak etiketlenir.
- Her satırın kendi dosyasındaki numarası ve bir − / + işareti vardır; kaldırılan satırlar kırmızı, eklenenler yeşildir, satır içindeki
  değişen karakterler işaretlenir, bir tarafta eksik olan satır taralıdır. Kod sözdizimi renklerini korur.
- Geçerli fark çerçevelenir; sayaç hangisi olduğunu gösterir (2 / 5). F2 / Shift+F2, Alt+↓ / Alt+↑ (veya metnin
  dışındayken j / k, ya da oklar) sonraki / önceki farka gider, sonuncudan ilkine döner. Bir farkın satırına tıklamak
  onu geçerli fark yapar. Benzer satırlar, üstlerine satır eklenmiş olsa bile yan yana durur.
- Sağ kenardaki şerit her farkı dosyadaki yerinde gösterir (yeşil eklenen, kırmızı kaldırılan, kehribar
  değişen); tıklamak oraya götürür.
- **Büyük/küçük harf yok say** ve **Boşlukları yok say** hemen yeniden karşılaştırır. *⋯* menüsünün *Karşılaştırma seçenekleri* ayrıca boş
  satırları, satır sonlarını (CRLF / LF) veya tüm boşlukları yok sayar, taşınan satırları işaretler (↕), benzer satırları yan yana koyar ve
  farkları sözcüklere göre işaretler. Bunlar Yapılandırma › İşlem › **Karşılaştır** (*Karşılaştırma ayarları...*) ayarlarıdır:
  sonraki pencere için de kalırlar. Orada ayrıca: satır süzgeçleri (tüm satırları bir düzenli ifadeyle eşleşen bir fark
  soluk gösterilir ve ziyaret edilmez, ör. yorum satırları için `^\s*//`), değiştirmeler (ör. sürüm numaralarının
  eşit sayılması), yazı tipi, ilk kayıtta bir yedek (`name.bak`) ve iki yerel dosya için *İçeriğe göre karşılaştır*
  yerine açılan harici bir karşılaştırma / birleştirme programı.
- İki taraf da yerinde düzenlenir: dosyalardan birine yazın, kısa bir duraklamadan sonra ikisi yeniden
  karşılaştırılır. Taralı satırlar diğer dosyanın satırlarını temsil eder ve üzerlerine yazılamaz.
- **Bir farkı diğer tarafa kopyalama**: Alt+→ (soldan sağa) / Alt+← (sağdan sola), çubuğun ok düğmeleri
  veya her farkın ilk satırındaki kenar boşluğundaki → / ← oku. *⋯* menüsü ayrıca tüm farkları
  bir seferde kopyalar, tarafları değiştirir, bir dosyayı yeniden yükler, metin bulur (Ctrl+F) ve bir satıra gider (Ctrl+G).
- **Geri al / Yinele** (Ctrl+Z / Ctrl+Y) iki dosya üzerinde birlikte ileri geri gider; bir yazma dizisi tek adımdır.
- **Kaydet** değişen dosyaları yazar (Ctrl+S düzenlenen tarafı, Ctrl+Shift+S ikisini), her biri kendi kodlaması,
  bayt sırası işareti ve satır sonlarıyla. Her dosya adının yanındaki düğme (*UTF-8 · CRLF*) nasıl yazılacağını değiştirir. Oraya
  yazılamayan bir dosya (örneğin bir arşivin içindeki) *Salt okunur* olarak işaretlenir. Kaydedilmemiş değişikliklerle kapatırken önce
  sorar; başka bir programın değiştirdiği dosya yeniden yüklenir (kaydedilmemiş değişiklikleri varsa önce sorulur).
- Her dosya adının yanındaki klasör düğmesi o taraf için başka bir dosya seçer.
- **Karşılaştır** (F5) kaydedilmemiş bir şey yokken dosyaları yeniden okur, aksi hâlde metinleri şu anki hâlleriyle karşılaştırır.
- *⋯* menüsü farkları bir **yama** (birleşik fark: sol dosya eski, sağ dosya
  yeni) veya **HTML raporu** (yan yana, değişen karakterler işaretli) olarak kaydeder.
- Durum satırı iki dosyanın satır sayısını, imlecin satır ve sütununu, dili ve
  kodlamayı gösterir. Esc pencereyi (veya aramayı) kapatır.
- **İkili dosyalar** yan yana hex dökümü olarak gösterilir: iki tarafta aynı ofsette satır başına 16 bayt, farklı
  baytlar işaretli; farklı satırlar herhangi bir fark gibi ziyaret edilir (8 MB'tan büyük dosyalar oraya kadar
  gösterilir).
- **Belgeler** (Word, Excel, PowerPoint, OpenDocument, eski .doc / .xls / .ppt, PDF) metinleriyle karşılaştırılır:
  paragraflar, tablo satırları (hücreler sekmeyle ayrılır), PDF sayfaları `── n ──` ile işaretlenir. Salt okunur.
- **Resimler** (PNG, JPEG, BMP, GIF, WebP, ICO): yan yana, üst üste (sol ile sağ arasında bir kaydırıcı)
  veya farkları (farklı pikseller soluk sol resmin üzerinde kırmızı, küçük renk değişiklikleri için bir toleransla);
  durum satırı farklı pikselleri sayar.

## Dosyaları birleştir

*Araçlar > Dosyaları birleştir...* 3 yönlü bir birleştirmedir. Üç dosya işaretleyin (ortak ata ve ondan türeyen iki sürüm; bir
liste hangisinin ata olduğunu sorar): yalnızca bir tarafta yapılan değişiklikler kendiliğinden birleştirilir, iki
tarafın da farklı değiştirdiği yerler **çakışmalardır** ve sonuçta git'in işaretlediği gibi işaretlenir. Ya da imleci
çakışma işaretleri olan bir dosyaya (durmuş bir git birleştirmesi) getirip onları yerinde çözün.

**İki** dosya işaretliyken (veya her panelin imlecinin altındaki dosya) karşılaştırma penceresi açılır: ortak ata
olmadan bir satırı hangi tarafın değiştirdiğini kimse söyleyemez, bu yüzden her fark diğer tarafa elle kopyalanır (Alt+← /
Alt+→), iki taraf da düzenlenebilir ve *Kaydet* ikisini de yazar.

- Çubuk kaç çakışmanın kaldığını söyler. ↑ / ↓ (Alt+↑ / Alt+↓) değişiklikten değişikliğe gider ("Değişiklik 2 / 4"),
  **Sonraki çakışma** (F8) henüz çözülmemiş sonrakine gider.
- Üç dosya blok blok, hizalı gösterilir (bir tarafta olmayan satırlar taralıdır): sol tarafın değişiklikleri
  mavi, sağ tarafınkiler mor, iki tarafta aynı değişiklik yeşil, çakışmalar turuncu. Her değişiklik sonucun neyi aldığını söyler:
  *Otomatik* (birleştirmenin kendiliğinden aldığı) veya *Sol / Temel / Sağ / İkisi alındı*. Bir değişikliğe tıklamak onu
  geçerli yapar.
- Geçerli değişiklik için **Sol** (Ctrl+1), **Temel** (Ctrl+2), **Sağ** (Ctrl+3) tarafı veya **İkisini** (Ctrl+4, sol olan
  önce) **alın**; birleştirmenin kendiliğinden aldığı bir değişiklik için de; çözülen bir çakışma sonrakine geçer.
  **Geri al** (Ctrl+Z) bir seçimi geri alır.
- Aşağıdaki sonuç her bloğu geldiği yerin renginde gösterir; çözülmemiş bir çakışma işaretlerini korur.
- **Kaydet** (Ctrl+S) sonucu iki sürümün ilkine (veya işaretli dosyaya) kendi
  kodlaması ve satır sonlarıyla yazar; çözülmemiş çakışmalar işaretleriyle içinde kalır (ileti kaç tane olduğunu söyler). Kaydedilmemiş
  değişikliklerle kapatırken önce sorar. Birleştirmenin renkleri renk temalarındadır (`Merge.*` anahtarları).
- Git eklentisinin *Çakışmalar...* komutu çakışmalı bir dosyayı burada açar ve çakışmasız kaydedildiğinde onu hazırlar.

## Yinelenen dosyalar

*Araçlar > Yinelenen dosyalar...* birden fazla kez bulunan dosyaları bulur. Dosyalar sekmesinin yanında bir
**Yinelenenler** sekmesiyle kendi başına bir sayfa olarak açılır (*Klasörleri eşitle* gibi): dosyalar sekmesi açıkken sizi
panellere geri götürür; ✕ düğmesi, sekmesinin × düğmesi veya Esc onu kapatır. **Dosyaya git** ve **Panele aktar** dosyaları gösterir.

![Yinelenen dosyalar](../screenshots/duplicates.png)

- Aranacak klasörleri seçin (başlangıçta iki panelin klasörleri): her biri ✕ içeren bir çiptir; **Klasör ekle** ve
  **Panel klasörleri** daha fazlasını ekler.
- İçeriğe göre, ad ve boyuta göre veya yalnızca ada göre karşılaştırın; bir dosya maskesi ve en küçük boyut belirleyin.
- Aramadan sonra dört sayı özetler: yinelenen gruplar, yinelenen dosyalar, boşaltabileceğiniz alan ve
  taranan dosyalar.
- Sonuçlar gruplar hâlinde gelir, en çok alan harcayan grup ilk sırada. Her grup dosyanın boyutunu, kaç kopya
  olduğunu ve fazladan kapladıkları alanı gösterir; katlamak için başlığına tıklayın (veya Space / Enter) ya da **Tümünü kapat**.
- Dosyaları elle ya da **Akıllı işaretle** ile işaretleyin: her grupta en yeniyi, en eskiyi, en kısa yolu veya en uzun yolu
  tut gibi. Her dosya *Kalsın* veya *Kaldır* gösterir. Kurallar her dosyanın en az bir kopyasını her zaman bırakır; bir grubun
  tüm kopyalarını kendiniz işaretlediyseniz uyarılırsınız.
- Sonra **Çöp kutusuna taşı**, **Kalıcı sil**, **Klasöre taşı** veya **Panele aktar**.

## Klasör boyutları

*Araçlar > Klasör boyutları...* bir klasörde alanı neyin kapladığını gösterir; sunucularda ve bulut sürücülerinde de. Diğer panelin
yerinde açılır ve yanındaki panelin klasörünü gösterir: orada başka bir klasöre gidin, yeniden taranır. *Yeniden tara* (F5) onu yeniden okur; ✕ düğmesi veya Esc paneli geri getirir. Başlık satırındaki **Ekranı kapla** düğmesi onu, taramasıyla birlikte, sekmeli kendi
sayfasına taşır (yinelenen dosyalar gibi): iki panel de dosyalar sekmesinin altında geri gelir ve *Panele geri koy* (aynı yerdeki
düğme) onu yeniden diğer panelin üstüne koyar; sekmesinin ×, ✕ veya Esc düğmesi kapatır. Yanında panel olmayan kendi sayfasında *Klasör...* başka bir klasörü tarar (klasör ağacında seçilir, sunucularda da).

![Klasör boyutları](../screenshots/disk-usage.png)

- **En üstte:** gösterilen klasörün yolu (geri çıkmak için bir parçaya tıklayın), boyutu, dosyaları ve klasörleri, ada göre bir
  **süzgeç** (Ctrl+F; eşleşen adları ve onlara giden klasörleri tutar), *Klasör...* (kendi sayfasında)
  ve *Yeniden tara* / *Durdur*.
- **Altında:** seçili dosya veya klasör boyutu ve payıyla, ve onunla yapılabilecekler: **Haritada aç**
  (bir klasöre yakınlaş), **En büyüğe git** (içindeki en büyük dosya), **Panelde göster**, **Aç**, **Çöp kutusuna taşı**.
  Silme orada sorar (*Kalsın* / *Çöp kutusuna taşı*); Shift+Delete kalıcı olarak siler.
- **Ağaç:** klasörler ve dosyalar en büyükten en küçüğe. Bir sütun başlığına tıklamak ada, boyuta, dosya
  sayısına veya tarihe göre sıralar, yeniden tıklamak ters çevirir. Bir klasörün çubuğu içindeki türleri renkleriyle gösterir. ▸ / → bir
  klasörü açar, ← kapatır; çift tık veya Enter bir klasöre yakınlaşır (bir dosya: onu panelde gösterir), Backspace yukarı çıkar.
- **Dosya türleri** (sağda): kategoriler (video, ses, programlar, arşivler, resimler, belgeler, kod ve veri, diğer)
  boyutları ve paylarıyla; ▸ uzantılarını gösterir. Bir kategoriye veya uzantıya tıklamak dosyalarını
  haritada vurgular, *Tümünü göster* bunu sonlandırır.
- **Harita** (altta): her dosya, türünün renginde bir dikdörtgendir ve ne kadar büyük olduğuna göre boyutlandırılır; yeterince büyük klasörlerin
  adını ve boyutunu içeren bir şeridi vardır. Ağaçta bulmak için bir dikdörtgene tıklayın; çift tık
  klasörüne yakınlaşır, *Yukarı ...* geri gider. Araç ipucu ve alt bilgi işaretçinin altındaki öğeyi gösterir.

## Sağlama toplamları

*Dosya > Sağlama toplamı*:

- **Sağlama toplamı oluştur (MD5/SHA)...:** algoritma kartlardan seçilir: MD5, SHA-1, **SHA-256** (önerilen: indirme
  sayfalarının ve Linux imajlarının yayımladığı), SHA-512, BLAKE3 (en hızlısı), CRC32 (bir .sfv dosyası; yalnızca kopyalama hatalarını bulur)
  ve SHA-384 ve sistem destekliyorsa SHA-3. Kartların altındaki bir satır her birinin ne işe yaradığını söyler; eski olanlar
  işaretlidir. Sonuç **tek bir sağlama dosyasına** (adı algoritmanın uzantısını izler; **…** başka bir klasör seçer),
  **her dosyanın yanında bir dosyaya** veya **yalnızca panoya** (dosya yazılmaz) gider. **Önizleme**
  satırları *Standart* biçimde (`hash *name`, sha256sum ve çoğu araç okur) veya *BSD biçiminde*
  (`SHA256 (name) = hash`, macOS ve BSD) gösterir. **Oluştur** dosyaları iletişim kutusunda ilerlemesiyle okur, sonra
  sağlama toplamlarını gösterir (*Kopyala* kopyalar); **Bitti** kapatır. Önizlemenin altına yapıştırılan bir **beklenen sağlama toplamı**
  sonuçla karşılaştırılır (*Eşleşiyor* dosyayı adlandırır; başka uzunlukta olan, o algoritmayı önerir); önceden yapıştırılırsa
  yalnızca bir algoritmada o uzunluk varsa uzunluğuna göre algoritmayı seçer.
- **Sağlama toplamlarını doğrula:** seçili sağlama dosyalarını (md5sum / sha256sum, BSD ve SFV satırları) kendi
  penceresinde okur. En üstte **karar** ("Dosya sağlam", "12 dosyanın 2'sinde sorun var"; denetlenirken ilerleme,
  **Yeniden denetle** veya F5) algoritma, sağlama dosyası ve denetlendiği zamanla birlikte. Aşağıda her dosya için bir satır, önce
  sorunlular: **Bozuk** (sağlama toplamı farklı: beklenen ve gerçek olan, farklı basamaklar işaretli),
  **Eksik** (listede var, bulunamadı), **Okunamayan** (nedeniyle, ör. kullanımda) ve **Tamam**. Birden fazla dosyada
  çipler duruma göre, bir kutu ada göre süzer (Ctrl+F). Bir satırın düğmeleri dosyayı panelde gösterir, sağlama toplamını kopyalar
  veya yeniden denetler; bir tık satırları seçer (Ctrl+tık daha fazlasını). Ana düğme panelde seçili satırları seçer,
  yoksa sorunlu dosyaları, o da yoksa hepsini; **Raporu kaydet** her sonucun bir metin dosyasını yazar.

## Böl ve birleştir

*Dosya > Böl / birleştir*:

- **Dosyayı böl...** büyük bir dosyayı `.001`, `.002`, … parçalarına keser ve özgün
  adı, boyutu ve sağlama toplamını içeren küçük bir `.crc` dosyası ekler.
- **Parçaları birleştir...** parçaları yeniden birleştirir ve sonucu `.crc` dosyasıyla denetler.

## Diğer dosya araçları

- **Öznitelikleri değiştir...** (*Dosya*): salt okunur, gizli, sistem ve arşiv bayrakları ve değiştirilme zamanı,
  alt klasörler için de.
- **İzinler...** (*Dosya*): seçili öğelerle kimin ne yapabileceği.
  - Linux, macOS ve SFTP'de: sahip, grup ve diğerleri için okuma / yazma / çalıştırma, özel bitler, sekizlik değer,
    sahip ve grup, ve klasörlerde içindeki her şey de.
  - Bir Windows dosyası veya klasörü kendi penceresini açar. Solda erişimi olanlar vardır: öğenin kendi kuralları, sonra
    üstteki klasörden devralınanlar (her kullanıcı veya grup bir kez), *Değiştirme* veya *Reddet · Yazma* gibi bir düzeyle. Sağda
    seçili kural vardır: *İzin ver* veya *Reddet*, nerede geçerli olduğu (bu klasör, alt klasörler, dosyalar), erişim düzeyi,
    temel haklar onay kutuları olarak ve içerdikleri gelişmiş haklar. Devralınan bir kural üstteki klasörde değiştirilir
    (*Aç*), ya da devralma burada kapatılır: devralınan kurallar o zaman öğenin kendi kuralları olarak saklanır veya kaldırılır.
  - O pencerede ayrıca: *Ekle* (bir kullanıcı veya grup, yazarken önerilir; bilinmeyen bir ad bildirilir), *Kaldır*, sahip
    (*Değiştir*, yönetici hakları gerekir) ve oturum açmış kullanıcının etkin erişimi.
  - *Tüm alt klasörlerde ve dosyalarda da değiştir* içindeki her şeyin kendi kurallarını kaybetmesini ve klasörünkini almasını sağlar.
    *Uygula*'dan önce hiçbir şey değişmez; *Sıfırla* düzenlemeleri geri alır.

  ![Windows izinleri](../screenshots/permissions.png)

- **Özellikler...** (Alt+Enter): Windows'ta Gezgin'in özellikler penceresi; başka yerlerde NextCommander'ın kendi penceresi
  tarihler, öznitelikler, Unix izinleri ve klasör boyutuyla.
- **Kaplanan alanı hesapla...** (Ctrl+L): seçimin toplam boyutu ile dosya ve klasör sayısı.
- **Açıklama düzenle...** (Ctrl+Alt+Z): bir dosya için, diğer dosya yöneticilerinin de okuduğu 4DOS biçimindeki `descript.ion` dosyasında saklanan bir açıklama.
  *Göster > Görünüm > Açıklamalar* (Ctrl+Shift+F2) bunları gösterir.
- **Kodla / çöz:** MIME (Base64), UUE ve XXE.
- **Yazdır:** dosya listesini yazdırın ya da seçili dosyaları yazıcıya gönderin.
- **İlişkilendirmeler:** bir dosya türünü Enter ile hangi programın, ya da Lister'ın veya düzenleyicinin açacağını seçin.
  Shift+Enter (*Birlikte aç...*) bir program listesi sunar.
- **Arka plan aktarım yöneticisi...** (*Komutlar*): çalışan ve bekleyen işler hızları ve kalan süreleriyle;
  duraklat, geçerli dosyadan sonra durdur veya iptal et.
- **Sistem bilgisi...** (*Komutlar*): programın sürümü, işletim sistemi, bilgisayar, kullanıcı, işlemci
  çekirdekleri, bellek, çalışma süresi, ayarlar klasörü (kopyala veya aç) ve her sürücü ne kadar dolu olduğuyla. **Ayrıntıları kopyala**
  (Ctrl+C) bir sorun raporu için hepsini metin olarak kopyalar.
- **Dal görünümü** (Ctrl+B): tüm alt klasörlerin tüm dosyaları tek bir listede. Ctrl+Shift+B aynısını yalnızca seçili
  klasörler için yapar.

## Dahili terminal

Terminali kendi başına bir sayfa olarak açmak için Ctrl+` tuşuna (veya *Komutlar > Dahili terminal* komutuna ya da düğme çubuğunun dahili terminal düğmesine) basın: dosyalar sekmesinin yanında bir **Terminal** sekmesi belirir. O sayfada Ctrl+` dosyalara geri döner (kabuklar çalışmaya devam eder) ve yeniden basınca terminale gider. Telnet, SSH, rsh ve seri oturumlar da orada açılır.

**Komut satırının** sonundaki terminal düğmesi onu bunun yerine panellerin altında, **pencerenin altında** açar;
orada odaktayken Ctrl+` onu gizler (kabuk çalışmaya devam eder).

- Terminalin **sekmeleri** başlığının yanında durur; sığandan fazla olduğunda yana kayar (fare
  tekerleğiyle; etkin sekme görünüme getirilir) ve sağdaki düğmeler yerinde kalır.

- Gerçek bir terminaldir: renkler ve vim, mc, top ve less gibi tam ekran programlar çalışır ve Ctrl+C çalışan
  programı durdurur.
- Etkin panelin klasöründe başlar. **Panel klasörü** onu geçerli panel klasörüne taşır, **Temizle**
  ekranı temizler, **Durdur** kabuğu yeniden başlatır.
- **Sekmeler:** `+` başka bir terminal açar, `▾` kurulu kabukları listeler (Windows'ta cmd, Windows PowerShell,
  PowerShell 7, Git Bash ve WSL; başka yerlerde kabuklarınız). `exit` veya sekmenin `×` düğmesi kapatır.
- *error*, *failed*, *warning*, *done* gibi sözcükler, program bunları kendi rengi olmadan yazdığında çıktıda renklendirilir (kırmızı, kehribar, yeşil).
  *Yapılandırma > Çeşitli > Dahili terminal* bunu kapatır ve sözcükleri
  ile renkleri düzenler (tam sözcükler, büyük/küçük harf yok sayılır; eşleşen ilk girdi kazanır).
- **Parçacıklar ▾** kayıtlı komutlarınızı listeler: bir tık birini etkin sekmeye yazar (Enter ile, ya da *Ardından Enter'a bas* kapalıysa
  satırı bitirmeniz için satırda bırakılır; *Hepsine yaz* ile işaretli her sekmeye). *Kayıtlı komutları düzenle...*
  listeyi Yapılandırma penceresinin *Çeşitli* sayfasında açar (menüdeki ad, komut, Enter).
- **İzle** (bir SSH sekmesi) sunucusunun işlemci, bellek ve kök disk kullanımını, ağ hızlarını, yükü
  ve çalışma süresini gösteren bir çubuk gösterir; oturumun oturum açma bilgisiyle ikinci bir SSH bağlantısı üzerinden her 3 saniyede okunur (Linux sunucuları:
  /proc okunur; diğerleri sayaç olmadığını söyler). Her SSH sekmesinin kendi çubuğu vardır; çubuk etkin sekmeyi izler.
- **Böl ▾** iki sekmeyi yan yana veya alt alta ya da dördünü (ikişer ikişer) gösterir: etkin sekme ve
  sonrakiler (yeterli sekme yoksa yeni kabuklar). Etkin bölme çerçevelenir ve tuşları ve düğmeleri alır;
  başka bir bölmeye tıklamak onu etkin yapar ve sekme çubuğundan seçilen bir sekme etkin bölmenin yerini alır.
  *Tek terminal* geri döner.
- **Hepsine yaz** aynı anda birkaç sekmeye yazar: açıkken (● Hepsine yaz) her sekmenin bir onay kutusu olur ve
  bir sekmede yazılan tuşlar, yapıştırılan metin ve komut satırları işaretli her sekmeye de gider (ör. aynı komut birkaç
  sunucuda). Terminalin kendi yanıtları kendi sekmesinde kalır; satır konsolu sekmesi tam satırları alır.
- **Bul** (terminalde Ctrl+Shift+F) çıktının üzerinde bir arama çubuğu açar: geri kaydırma belleğindeki ve
  ekrandaki eşleşmeler işaretlenir, en yenisi ilk gösterilir; Enter (↑) eskilere, Shift+Enter (↓) yenilere gider, uçlarda
  başa döner ve sayaç nerede olduğunuzu gösterir. **Aa** büyük/küçük harfi eşleştirir. Esc kapatır.
- **Kayıt** etkin sekmenin çıktısını şu andan itibaren bir metin dosyasına yazar (*Bu sekmeyi dosyaya kaydet*): renksiz ve denetim kodsuz
  düz metin, başta ve sonda oturumu ve zamanı içeren bir satır. Bir sekme kaydedilirken düğme kırmızı **● Kayıt**
  gösterir ve ipucu dosyayı adlandırır; *Kaydı durdur* dosyayı sonlandırır, sekmeyi kapatmak da. *Kayıt klasörünü aç* klasörü etkin panelde gösterir.
  Dosyalar sekmenin adından (bir SSH / Telnet oturumu için bağlantısından) ve zamandan adlandırılır; ayarlar klasörünün `logs/terminal` klasöründe
  ya da *Çeşitli* sayfasında belirlenen klasörde bulunur; o sayfa her yeni sekmeyi de kaydedebilir.
- Panellerin altındayken yüksekliğini değiştirmek için üst kenarını sürükleyin. □ düğmesi (veya başlığına çift tık) onu
  dosyalar sekmesinin yanında bir **Terminal** sekmesiyle kendi sayfası yapar; kabukları çalışmaya devam ederken dosyalar sekmesi sizi panellere geri götürür.
  Sekmenin `×` düğmesi (veya ❐) onu panellerin altına geri koyar.
- Terminal odaktayken tüm tuşlar kabuğa gider; yalnızca Ctrl+` dosyalara döner. Ctrl+Shift+C / Ctrl+Shift+V
  kopyalar ve yapıştırır, sağ tık seçimi kopyalar veya yapıştırır. Fare tekerleği ve Shift+Page Up / Page Down
  geri kaydırır.
- Varsayılan kabuk ve terminal yazı tipi, [Yapılandırma penceresinin](configuration.md) *Çeşitli* ve *Görünüm > Yazı tipi* sayfalarında ayarlanır. Açık terminal sekmeleri
  NextCommander'ı bir sonraki başlatışınızda geri gelir.

*Komutlar > Komut istemi penceresini aç* (Ctrl+Alt+T) bunun yerine sisteminizin kendi terminal penceresini geçerli
klasörde açar. Artık varsayılan düğme çubuğunda yok; *Yapılandırma > Düğme çubuğu...* onu ekleyebilir.

## Dosya indeksi

NextCommander dosyalarınızın kendi indeksini tutabilir; böylece aramalar, yinelenen dosyalar ve klasör boyutları
neredeyse anında yanıtlanır. Varsayılan olarak açıktır (*Yapılandırma > Seçenekler > İşlem > Dosya indeksi*).

- Windows'ta tüm sabit sürücüleri, Linux ve macOS'ta ana klasörünüzü indeksler; başka klasörler seçebilir ve
  bazılarını dışarıda bırakabilirsiniz (örneğin `node_modules`). Çöp kutusu ve sistem klasörleri her zaman dışarıda bırakılır.
- İlk tarama arka planda düşük öncelikle çalışır ve birkaç dakika sürebilir. Sonrasında yalnızca değişen
  klasörler okunur ve NextCommander çalışırken değişiklikler izlenir.
- NextCommander kapalıyken olan değişiklikleri bulma:
  - Varsayılan olarak bir klasör, zamanı değiştiğinde yeniden okunur. Bu, içine bir şey eklendiğinde,
    kaldırıldığında veya yeniden adlandırıldığında olur, ancak yalnızca bir dosyanın içeriği değiştiğinde olmaz; böyle bir dosya indekste eski boyutunu ve
    tarihini korur (ada göre aramalar etkilenmez).
  - **Başlangıçta NTFS değişiklik günlüğünü kullan** (Windows, varsayılan olarak açık): diskin kendi değişiklik listesi
    yeniden okunacak klasörleri, içeriği değişen dosyalar dahil, adlandırır. Yönetici hakları gerekmez. Liste
    söyleyemediğinde (başka bir dosya sistemi ya da listenin başa sarmasına yol açan çok sayıda değişiklik), klasör zamanları
    karşılaştırılır.
  - **Her klasörü yeniden denetle (saat)**: bu kadar saat sonra her klasör, herhangi bir sistemde yeniden okunur. Bu,
    ilk tarama kadar sürer; 0 (varsayılan) kapatır.
- Ayarlar sayfası indekslenen dosya ve klasör sayısını ile son güncellemeyi gösterir ve bir **Yeniden oluştur**
  düğmesi vardır.
- *Dosya bul* bunu **Dosya indeksini kullan** seçeneğiyle kullanır. Everything (Windows), plocate (Linux) veya
  Spotlight (macOS) varsa Dosya bul bunları da kullanabilir.
