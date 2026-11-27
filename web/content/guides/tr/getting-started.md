# Başlarken

[README'ye dön](../../README.md)

Bu kılavuz ana pencereyi, pencerede nasıl gezineceğinizi, dosyaları nasıl seçeceğinizi ve günlük dosya işlemlerini
nasıl yapacağınızı anlatır.

## Pencere

![Ana pencere](../screenshots/main-light.png)

Yukarıdan aşağıya:

- **Menü çubuğu:** Dosya, Düzenle, İşaretle, Komutlar, Bağlantı, Göster, Yapılandırma, Başlat ve sağda Yardım.
- **Düğme çubuğu:** sık kullanılan komutlar için tek tıklık düğmeler (dahili terminal kendi sekmesiyle bir sayfa olarak
  açılır; sonda bağlantı yöneticisi ve ayarlar). Üzerinde herhangi bir yere (bir düğmeye ya da boş kısma) sağ tıklayarak
  düğme ekleyebilir, değiştirebilir veya kaldırabilirsiniz; ya da *Yapılandırma > Düğme çubuğu...* menüsünü kullanın.
  İki panelin arasında ikinci bir dikey düğme çubuğu bulunur.
- **Sayfa sekmeleri:** seçenekler, bağlantı yöneticisi veya dahili terminal dosya panellerinin yanında açıkken bir sekme
  şeridi sayfaları gösterir: dosyalar sekmesi (etkin panelin klasörü, bulunduğu yerin simgesiyle: Karşıdan yüklenenler
  gibi bir kullanıcı klasörü için sistemin simgesi, bir sürücü, bir sunucunun ürün işareti, bir arşiv ...),
  **Seçenekler** ve **Terminal**. Tıklamak sayfa değiştirir; `nc_SwitchWorkspaceTab` sonraki sayfaya geçer. Dosyalar
  dışında bir sayfa gösterilirken panellerle çalışan menü öğeleri, düğmeler, sürücü çubuğu, işlev tuşları, komut satırı
  ve kısayollar kapatılır; Yapılandırma, Başlat, Yardım, terminal, bağlantı yöneticisi, sistem bilgisi ve Çıkış yine
  çalışır.
- **Sürücü düğmeleri:** her sürücü için bir düğme. Sağ uçta ana klasörünüze, Belgeler'e ve İndirilenler'e kısayollar
  vardır (Masaüstü *Yapılandırma > Seçenekler > Düzen* içinden açılabilir). Bir USB disk, hafıza kartı veya telefon
  taktığınızda ya da çıkardığınızda sürücüler bir iki saniye içinde kendiliğinden güncellenir; çıkarılmış bir sürücüyü
  gösteren panel hâlâ var olan en yakın klasöre geçer (Windows'ta ana klasörünüze).
- **Telefonlar ve kameralar:** USB ile "Dosya aktarımı" kipinde (MTP) bağlanan bir telefon, telefon simgeli bir sürücü
  düğmesi alır. İlk düzeyi telefonun depolama alanlarıdır (dahili depolama, SD kart); kopyalama, taşıma, yeni klasör,
  yeniden adlandırma ve silme bir diskteki gibi çalışır. Telefonun kilidini açın ve sorduğunda dosya aktarımına izin
  verin ("Telefon verilerine erişime izin verilsin mi?"): o zamana kadar telefon hiçbir depolama alanı göstermez ve
  NextCommander, siz yanıtlarken bunu söylemeden önce telefonu birkaç kez yeniden açar. Linux'ta telefon, masaüstünün
  (GNOME) onun için bağladığı klasör olarak görünür; diğer masaüstlerinde (KDE ...) ve macOS'ta NextCommander telefonla
  libmtp üzerinden kendisi konuşur (Linux: `libmtp` paketi, NextCommander'ın paketiyle birlikte kurulur; macOS:
  uygulamanın içinde gelir). macOS'ta önce Fotoğraflar'ı, Görüntü Yakalama'yı ve Android File Transfer'i kapatın:
  telefonu ilk alan program onu elinde tutar.
- **Sürücü kutusu:** geçerli sürücü, etiketi ve boş alanıyla birlikte, ayrıca `\` (kök) ve `..` (üst klasör)
  düğmeleri. Alt+F1 ve Alt+F2 sol ve sağ panelin sürücü listesini açar. Liste her sürücünün etiketini ya da ne olduğunu
  ("Yerel Disk", "Çıkarılabilir Disk", "Ağ Sürücüsü" ...) gösterir. Sürücülerin altında: `+` Bilgisayar, `\` Ağ ve
  `×` çöp kutusu. `+` girdisi sürücüleri panelde listeler (Bilgisayar görünümü): önce harf sırasıyla sürücüler, sonra
  harici diskler, sonra telefonlar, sonra çöp kutusu.
- **Sürücü simgeleri**, sürücülerin göründüğü her yerde (sürücü düğmeleri, sürücü kutusu, Bilgisayar görünümü)
  sistemin kendi simgeleridir: Windows'ta Gezgin'inkiler (Geri Dönüşüm Kutusu, Bu Bilgisayar ve Ağ da dahil), Linux'ta
  masaüstünün simge teması, macOS'ta Finder'ınkiler. *Yapılandırma > Seçenekler > Simgeler* bunları kapatabilir
  (o zaman yerleşik simgeler kullanılır).
- **Bir sürücüye sağ tıklamak** (sürücü düğmeleri, sürücü listesi, Bilgisayar görünümü) o sürücünün sistem menüsünü
  açar: Windows'ta Gezgin'inki (aç, çıkar, biçimlendir, özellikler ...). Shift+sağ tıklama ya da diğer sistemlerde:
  uygulamanın kısa bir menüsü.
- **Çöp kutusu / Geri Dönüşüm Kutusu:** sürücü çubuğunun sonundaki çöp kutusu düğmesi (*Yapılandırma > Seçenekler >
  Düzen* içinden gizlenebilir), sürücü kutusunun `×` girdisi ve Bilgisayar görünümünün sonu silinen dosyaları panelde,
  özgün adlarıyla ve silinme tarihiyle gösterir. Onları dışarı kopyalayabilir (F5), bir yere taşıyabilir (F6), eski
  yerlerine geri koyabilir (sağ tık menüsündeki *Çöp kutusundan geri yükle*), kalıcı olarak silebilir (F8) ya da çöp
  kutusunu boşaltabilirsiniz (sağ tık menüsü ya da çöp kutusu düğmesinin menüsü). macOS'ta öğeler geri konamaz (Finder
  yerlerini kendisi tutar) ve Çöp'ü okumak için Tam Disk Erişimi gerekir.
- **Klasör sekmeleri:** her panelin birkaç sekmesi olabilir. `+` düğmesi yeni bir sekme açar. Bir sürücünün kökündeki
  sekme sürücünün sistem simgesini (sabit, çıkarılabilir, ağ ...), bir sistem klasörü (Belgeler, İndirilenler,
  Masaüstü ...) kendi simgesini, diğer klasörler klasör simgesini gösterir (*Yapılandırma > Klasör sekmeleri >
  Sekmelerde simge göster*).
- **Yol satırı:** geçerli klasör. Oraya gitmek için yolun bir parçasına tıklayın, bir alt klasör seçmek için parçalar
  arasındaki küçük oka tıklayın ya da bir yol yazmak için boş alana tıklayın (veya Alt+D'ye basın).
- **İki panel:** dosya listeleri. Etkin panelin yol satırı vurgulanır; Tab iki panel arasında geçiş yapar. Klasörler
  `[ad]` olarak gösterilir ve `[..]` bir üst düzeye çıkar. Boyutlarını ayarlamak için aralarındaki ayırıcıyı sürükleyin;
  sağ tık menüsü sol panelin payını %20'den %80'e kadar onar onar ayarlar (yan yana).
- **Durum satırı:** her panelin altında, seçili ve tüm dosyaların boyutu ve sayısı.
- **Komut satırı:** bir komut yazıp Enter'a basın. Etkin panelin klasöründe çalışır.
- **İşlev tuşu çubuğu:** F3 Görüntüle, F4 Düzenle, F5 Kopyala, F6 Taşı, F7 Yeni klasör, F8 Sil, Alt+F4 Çıkış.
  Bu düğmelere tıklayabilir ya da tuşlara basabilirsiniz.

Bu parçaların her biri *Yapılandırma > Seçenekler > Düzen* içinden açılıp kapatılabilir.

Dosya menüsü ilgili komutları alt menülerde toplar (Arşiv, İlişkilendirmeler, Yazdır, Böl / birleştir,
Kodla / çöz, Sağlama toplamı):

![Dosya menüsü](../screenshots/menu-files.png)

Göster menüsü görünümleri, ağacı, Hızlı görünümü, panel düzenini, dosya süzgeçlerini, sıralamayı ve nelerin
gösterileceğini barındırır:

![Göster menüsü](../screenshots/menu-show.png)

## Gezinme

- **Yukarı / Aşağı, Page Up / Page Down, Home / End** imleci hareket ettirir.
- **Enter** imlecin altındaki klasörü (veya arşivi) açar ya da bir dosyayı kendi programıyla açar.
- **Backspace** veya **Ctrl+Page Up** üst klasöre gider; **Ctrl+Backspace** köke gider.
- **Tab** diğer panele geçer.
- **Alt+Left / Alt+Right** geri ve ileri gider; **Alt+Down** klasör geçmişini, **Alt+Shift+Down** ise en sık
  kullandığınız klasörleri gösterir.
- **Klasör geçmişi** penceresi klasörleri *Sabitlenenler*, *Bugün*, *Dün* ve *Daha önce* diye gruplar; oraya en son ne
  zaman gittiğinizi ve ne sıklıkta gittiğinizi gösterir. İçinde arama yapabilir, yalnızca tek bir sürücüyü
  gösterebilir ve klasör başına şunları yapabilirsiniz: en üste sabitlemek (Ctrl+P), diğer panelde (Shift+Enter) ya da
  yeni bir sekmede (Ctrl+Enter) açmak veya kaldırmak (Del). 1–9 ilk dokuz klasörü hemen açar. Bir klasörü kaldırmak ya
  da geçmişi temizlemek geri alınabilir; sabitlenen klasörler kalır.
- **En sık kullanılan klasörler** penceresi klasörleri oraya ne sıklıkla gittiğinize göre sıralar (eski ziyaretler daha
  az sayılır) ve her biri için bir çubuk gösterir. Birini bulmak için birkaç harf yazın: harfler yolun herhangi bir
  yerinde, sırasıyla olabilir ("dl" İndirilenler'i bulur) ve klasörün kendi adındaki bir eşleşme öne gelir. Enter onu
  açar, Shift+Enter diğer panelde, Ctrl+Enter yeni bir sekmede açar; Del onu listeden kaldırır (Geri al geri koyar).
  Hiçbir şey eşleşmediğinde Enter, yazdığınızı bir yol olarak açar.
- **Alt+F10** **klasör ağacı** penceresini açar: o ana kadar okunan klasörleri süzün, gizli klasörleri gösterin ya da
  gizleyin, Ana klasöre, Masaüstüne, Belgelere veya İndirilenlere atlayın ve altta seçilen klasörün yolunu görün. Bu
  bilgisayardaki bir klasör için her sabit sürücüyü gösterir (klasörün kendi sürücüsü ona kadar açılır); sağ tık menüsü
  klasör oluşturur, yeniden adlandırır, siler ya da birini diğer panelde veya yeni bir sekmede açar.
- **Ctrl+D** favori klasörlerinizin listesi olan **Hotlist**'i açar. *Göster > Gezinme > Bu klasörü Hotlist'e ekle...*
  geçerli klasörü ekler.
- *Yapılandırma > Hotlist'i düzenle...* **hotlist penceresini** açar. Solda girdiler bir ağaç olarak yer alır: **Ekle ▾**
  bir klasör, bir komut, bir alt menü ya da bir ayırıcı çizgi ekler; oklar (Alt+↑ / Alt+↓) bir girdiyi taşır, ← onu alt
  menüsünden çıkarır, çöp kutusu (Del) onu siler (Geri al ile); bir girdiyi taşımak için sürükleyin, bir alt menünün
  ortasına bırakarak onu içine koyun. Süzgeç eşleşen girdileri alt menüleriyle gösterir. Sağda seçili girdi yer alır:
  türü, adı (bir harfin önündeki `&` onu menü tuşu yapar, örn. `&Movie`; aynı ada veya tuşa sahip ikinci bir girdi
  işaretlenir), klasörü (gözat ya da geçerli klasör; `%USERPROFILE%` gibi ortam değişkenleri çalışır) ve isteğe bağlı
  olarak aynı anda açılan diğer panel için bir klasör; ya da parametreleriyle bir komut. Aşağıda girdinin içinde
  bulunduğu menünün önizlemesi vardır. Değişiklikler bir kopya üzerinde yapılır: **Tamam** onları kaydeder,
  **İptal** onları atar.
- **Ctrl+Left / Ctrl+Right** imlecin altındaki klasörü sol ya da sağ panelde açar.
- **Ctrl+U** iki paneli yer değiştirir. *Komutlar > Hedef = Kaynak* diğer panelin aynı klasörü göstermesini sağlar.
- **Hızlı arama:** bir ada atlamak için Ctrl+Alt tuşlarını basılı tutup adın ilk harflerini yazın. Yukarı / Aşağı önceki
  / sonraki eşleşmeye atlar. Yalnızca harflerin aramayı başlatmasını *Yapılandırma > Seçenekler > İşlem > Hızlı arama*
  içinden ayarlayabilirsiniz.
- **Hızlı süzgeç (Ctrl+S):** yalnızca eşleşen dosyaları göstermek için yazın; **Ctrl+Y** süzgeci temizler.

### Sekmeler

- **Ctrl+T** yeni bir sekme açar, **Ctrl+W** kapatır, **Ctrl+Shift+T** en son kapatılan sekmeyi yeniden açar.
- **Ctrl+Tab / Ctrl+Shift+Tab** sekmeler arasında geçer; **Ctrl+1 … Ctrl+9** bir sekmeye atlar.
- **Ctrl+Up** imlecin altındaki klasörü yeni bir sekmede açar.
- Bir sekmeyi taşımak için sürükleyin. Orta tık onu kapatır.
- Klasörüne artık ulaşılamayan bir sekme (silinmiş, sürücü çıkarılmış, sunucu kapanmış) yeniden gösterildiğinde kapanır;
  bir panelin son sekmesi hâlâ var olan en yakın klasöre gider.
- *Göster > Sekmeler* daha fazlasını sunar: bir sekmeyi kilitleyin (klasör değişiklikleri o zaman yeni bir sekmede
  açılır), yeniden adlandırın, yinelenen sekmeleri kapatın ve bir sekme kümesini kaydedin ya da yükleyin.

### Görünümler

- **Ctrl+F1** kısa görünüm (adlar birkaç sütunda), **Ctrl+F2** tam görünüm (boyut, tarih ve özniteliklerle),
  **Ctrl+Shift+F1** küçük resimler (Ctrl+fare tekerleği boyutlarını değiştirir).
- **Ctrl+F8** panelin içinde bir klasör ağacı gösterir.
- **Shift+F1** özel bir sütun seti seçer, örneğin fotoğraf veya müzik ayrıntılarıyla.
  Sütun setleri klasöre göre kendiliğinden de değişebilir (*Göster > Görünüm > Özel görünüm modları*).
- *Yapılandırma > Sütun setlerini düzenle...* **sütun setleri** penceresini açar: solda setler (Yeni set, Çoğalt,
  Sil), sağda seçili set adıyla birlikte; sütunları (başlık, alan, bir kaydırıcıyla genişlik, oklarla sıra; listeden
  bir alan ekleme); boyut, tarih ve öznitelik sütunlarının kalıp kalmayacağı; ölçekli çizilmiş bir önizleme; ona geçiş
  yapan klasörler (`*Pictures*` gibi yol maskeleri, Enter veya `;` ile eklenir); liste arka planı, sekme rengi ve
  simgesi; ve girerken ve çıkarken çalıştırılan komutlar. Değişiklikler bir kopya üzerinde yapılır: **Uygula** onları
  panellerde gösterir, **Tamam** uygular ve kapatır, **İptal** onları atar.
- **Ctrl+F3 … Ctrl+F6** ada, uzantıya, zamana ve boyuta göre sıralar. Ona göre sıralamak için bir sütun başlığına
  tıklayın.
- **Göster > Grupla** dosyaları gruplar halinde gösterir: **türe** (belgeler, resimler, arşivler ...), **tarihe**
  (bugün, dün, bu hafta daha önce ... eski yıllar) ya da adın **ilk harfine** göre; klasörler önce gelir. Bir grubun
  başlığı kaç dosyası olduğunu gösterir; üzerinde Enter ya da çift tıklama grubu katlar ve yeniden açar, Insert / Space
  tüm dosyalarını işaretler. Her sekme kendi gruplamasını tutar; *Gruplama yok* onu kapatır.
- **Ctrl+H** gizli ve sistem dosyalarını gösterir ya da gizler.

![Ağaç paneli ve küçük resim görünümü](../screenshots/tree-and-thumbnails.png)

Sol panelde satır sayısı ve kodlama sütunları olan özel bir sütun seti:

![Özel sütunlar](../screenshots/custom-columns.png)

## Dosyaları seçme

Çoğu işlem seçili (işaretli) dosyalar üzerinde, hiçbir şey seçili değilse imlecin altındaki dosya üzerinde çalışır.

- **Insert** veya **Space** imlecin altındaki dosyayı işaretler ve aşağı iner. Bir klasör üzerinde Space ayrıca
  boyutunu hesaplar.
- **Shift+Up / Shift+Down** hareket ederken işaretler.
- **Ctrl+A** (veya **Ctrl+Num +**) tümünü seçer; **Ctrl+Num -** tüm seçimi kaldırır.
- **Num +** `*.jpg` gibi bir kalıpla bir grubu seçer, **Num -** bir grubun seçimini kaldırır, **Num \*** seçimi
  tersine çevirir.
- **Alt+Num +** imlecin altındaki dosyayla aynı uzantıya sahip tüm dosyaları seçer.
- **Num /** önceki seçimi geri yükler.
- Fareyle: Ctrl+tıklama ve Shift+tıklama ya da boş bir alanda sürükleyerek bir seçim çerçevesi çizin.
- Varsayılan olarak **sağ fare düğmesi** dosyaları **işaretler** (klasik iki panelli dosya yöneticilerindeki gibi);
  bağlam menüsünü açmak için onu bir an basılı tutun. Bu, *Yapılandırma > Seçenekler > İşlem* içinden kapatılabilir.

İşaretle menüsü ayrıca seçimleri kaydeder ve yükler, dosya adlarını panoya kopyalar ve iki paneli karşılaştırır
(*Klasörleri karşılaştır*, Shift+F2); bu, diğer tarafta eksik ya da daha yeni olan dosyaları işaretler.

## Temel işlemler

| Tuş | Ne yapar |
|---|---|
| F3 | Dosyayı Lister'da görüntüler |
| F4 | Dosyayı düzenler; Shift+F4 yeni bir dosya oluşturur |
| F5 | Diğer panele kopyalar |
| F6 | Diğer panele taşır (ya da yeniden adlandırır) |
| Shift+F6 | Yerinde yeniden adlandırma: ad listede düzenlenir; Enter yeniden adlandırır, Esc vazgeçer, ↑ / ↓ yeniden adlandırıp önceki / sonraki dosyaya geçer. İmlecin altındaki dosyanın adına bir kez daha tıklamak da aynı şeyi yapar (tıklamadan bir an sonra, böylece çift tıklama yine de açar) |
| F7 | Yeni klasör (`a/b/c` iç içe klasörler oluşturur); Shift+F7 onu diğer panelde oluşturur |
| F8 veya Delete | Siler (geri dönüşüm kutusuna / çöp kutusuna) |
| Shift+Delete | Kalıcı olarak siler |
| Alt+F4 | Çıkış |

Kopyalarken veya taşırken bir iletişim kutusu hedefi gösterir. Hedef klasörü değiştirebilir ya da kopyalarken
yeniden adlandırmak için yeni bir ad veya `*.bak` gibi bir kalıp yazabilirsiniz.

- **Hedef:** açılır liste sakladığınız hedefleri, diğer paneli ve son hedefleri listeler. **+ F7** (veya F7) bir menü
  açar: *Geçerli klasörü ekle (Konum=0)* hedefi listenin başında tutar (*Geçerli klasörü çıkar* onu yeniden çıkarır) ve
  *Sık kullanılan klasörler (hotlist)* (ayrıca Ctrl+D) hotlist'ten bir klasörü hedef olarak alır. **Ağaç** (F10)
  hedef panelin bir klasörünü bir ağaçtan seçer.
- **Yalnızca bu türdeki dosyalar:** bir dosya süzgeci (`*.jpg *.png`, hariç tutmak için `| thumbs\`); açılır liste daha
  önce kullanılan süzgeçleri listeler. **+ F8** (veya F8) iletişim kutusunun üzerinde *Dosya bul* penceresini açar ve
  seçili klasörlerde (ya da kaynak klasörde) arar: herhangi bir ölçütle arayın, sonra **Sonuçları kopyala** düğmesine
  tıklayın. Bulunan dosyalar o zaman seçim yerine kopyalanır ve kaynak klasörün altındaki klasörleri korunur; süzgeç
  kutusu kaç tane olduğunu gösterir. Bir süzgeci yeniden yazmak seçime geri döner. Aynı satır boyutu ile dosya ve
  klasör sayısını gösterir (klasörler arka planda sayılır).
- **NTFS izinlerini kopyala** (Windows) ve **Doğrula** (kopyayı özgün dosyayla karşılaştırır).
- **Tamam** hemen kopyalar, **F2 Kuyruk** dosyaları kuyruğa ekler. Tamam veya F2 Kuyruk üzerinde sağ tıklama kopyalamak
  yerine taşır (ve Taşı iletişim kutusunda kopyalar).
- **Seçenekler >>** gelişmiş seçenekleri açar: hedef dosya zaten varsa ne yapılacağı (sor, tümünün üzerine yaz, tümünü
  atla, eski olanların üzerine yaz, kopyayı ya da hedefi yeniden adlandır, yalnızca daha büyük ya da yalnızca daha küçük
  dosyaları kopyala, eski hedefleri yeniden adlandır) (disk düğmesi yanıtı varsayılan olarak tutar, raptiye iletişim
  kutusunu her zaman bu seçeneklerle açar), okunamayan dosyaları atlama, salt okunur / gizli / sistem dosyalarının
  üzerine sormadan yazma ve hedef panelde işaretli her klasörün içine kopyalama.

Uzun işler bir **ilerleme penceresi** gösterir: durum ve yüzde, nereden ve nereye; tüm dosyalar ve kaç tanesinin
bittiği, çalıştığı, beklediği, atlandığı ve başarısız olduğu; geçerli dosya; hız ve son eğrisi, kalan süre, geçen süre
ve dosyalar; ve durumlarıyla dosya listesi (başarısız bir dosya hatasıyla; liste katlanır). **Duraklat** /
**Sürdür** (Space), **Dosyayı atla** (kısmen kopyalanan dosya silinir ve iş devam eder), **Bu dosyadan sonra dur**,
**Arka plan** (halkalı küçük bir pencere; ⤢ ayrıntıları yeniden gösterir) ve **İptal** (Esc; önce sorulur, kısmen
kopyalanan dosya silinir). Hız sınırı iş çalışırken değiştirilebilir. **Bitince kapat** kapalıyken pencere sonunda
özetle ve **Hedefi aç** ile açık kalır.

Bir dosya **yapılamadığında** (kopyalama, taşıma, silme ...) iş duraklar ve ilerleme penceresinin üzerinde bir soru
açılır. İşin nerede olduğunu (14 dosyadan 6.'sı), ne olduğunu sade sözcüklerle ve neler yapılabileceğini söyler:
- **Dosya kullanımda:** dosyayı açık tutan program adıyla belirtilir (Windows); programın kendi bugünkü günlüğü
  tanınır (onu atlayın). O programı kapattıktan sonra **Tekrar dene**.
- **İznin yok:** **Yönetici olarak yeniden dene** işi bittiğinde yönetici haklarıyla yapar (reddedilen sonraki
  dosyalar sormadan katılır); *Özellikler* dosyanın özelliklerini açar.
- Hedef sürücüde **yeterli alan yok**, **ağ bağlantısı koptu** (bir geri sayımdan sonra kendiliğinden yeniden dener;
  *Yeniden denemeyi durdur*), **dosya yok** (bu arada taşınmış ya da silinmiş).

Dosyanın klasörü, boyutu ve tarihi ile **teknik ayrıntılar** (işlem, hata numarası ve iletisi, zaman;
*Ayrıntıları kopyala*) da gösterilir. **Atla**, **Tekrar dene** (Alt+R), **İşlemi durdur** (Esc); **Bunun gibi sonraki
hatalar için de aynısını yap** ile Atla, aynı türdeki sonraki hataları sormadan atlar.

**Kuyruk** penceresi (F2 Kuyruk, Kopyala ve Paketle iletişim kutularındaki *Kuyruğa ekle*) işlerini sırasıyla arka
planda çalıştırır:

- En üstte durumu (çalışıyor, duraklatıldı, bitti) ile **Tümünü duraklat** / **Tümünü sürdür** (Space) ve hâlâ
  yapılacak tüm işlerin ilerlemesi, hızı ve kalan süre.
- **Aynı anda 1 / 2 / 3 çalıştır**: aynı anda kaç işin çalışacağı; tüm kuyruk için **Hız sınırı** (çalışan işler onu
  paylaşır); **Seçilenleri ekle** etkin panelde seçili dosyaları kuyruğa alır (diğer panele kopyalanır).
- **Sıradakiler**: iş başına bir kart (ne yaptığı ve ne kadarını, nereden nereye, öğe sayısı ve boyut), çalışanlar
  ilerlemeleri, geçerli dosya, hız ve kalan süreyle. Sırayı değiştirmek için bekleyen bir işi tutamacından sürükleyin
  (veya seçili olan üzerinde Alt+↑ / ↓); her kartın kendi düğmeleri vardır: duraklat / sürdür, **Sıradaki yap**,
  iptal et ya da kaldır (Delete).
- **Bitti**: biten, başarısız (hatasıyla) ya da iptal edilen işler ve bittikleri zaman; **Hedefi aç** klasörü etkin
  panelde gösterir; **Bitenleri temizle** listeyi boşaltır.
- En altta durum başına bir sayı ve **Bitince**: hiçbir şey yapma ya da pencereyi kapat.

*Komutlar > Arka plan aktarım yöneticisi...* çalışan her işi listeler.

### Düzenle menüsü

Düzenle menüsü günlük dosya komutlarını tuşlarıyla birlikte tek bir yerde toplar: Görüntüle, Düzenle, Yeni dosya,
Birlikte aç..., Arşiv olarak aç, Kopyala, Taşı / yeniden adlandır, Yerinde yeniden adlandır, Yeni klasör, Hedef
panelde yeni klasör, Sil, Kalıcı olarak sil, Panoya kopyala, Panoya kes, Panodan yapıştır ve
Liste dosyasını panelde göster....

### Pano

Ctrl+C, Ctrl+X ve Ctrl+V dosyaları kopyalar, keser ve yapıştırır ve sisteminizin dosya yöneticisiyle birlikte çalışır.
Pano bir web adresi tutuyorsa Ctrl+V onu indirmeyi önerir.

## Sağ tık

- **Windows'ta**, yerel dosyaların bağlam menüsü Windows Gezgini'nin kendi menüsüdür; "Birlikte aç", "Gönder",
  paylaşım ve kurulu programlarınızın girdileri bulunur. **Shift+sağ tıklama** bunun yerine NextCommander'ın kendi
  menüsünü açar. Gezgin menüsünü *Yapılandırma > Seçenekler > İşlem* içinden ("Sistemin bağlam menüsünü göster")
  kapatabilirsiniz.
- **Linux ve macOS'ta**, ayrıca arşivlerdeki ya da sunuculardaki dosyalar için NextCommander'ın kendi menüsü açılır.

Varsayılan ayarlarla kısa bir sağ tıklamanın dosyayı işaretlediğini unutmayın; menüyü açmak için düğmeyi basılı tutun.
macOS'ta fareyle sağ tıklama (iki parmak ya da Ctrl+tıklama) her zaman menüyü açar ve Cmd+tıklama işaretler.

## Sürükle ve bırak

- Dosyaları kopyalamak için diğer panele sürükleyin; bunun yerine taşımak için **Shift** tuşunu basılı tutun.
- Bir klasörün üzerine bırakmak içine kopyalar; yol satırının bir parçasına bırakmak o klasöre kopyalar.
- Dosyaları diğer programlara ve sisteminizin dosya yöneticisine ya da onlardan sürükleyin.
- Bir dosyayı silmek için F8 Sil düğmesine bırakın.
- Bir program için düğme oluşturmak üzere programı düğme çubuğuna bırakın.

## Komut satırı

Bir panel odaktayken yazdığınız her şey komut satırına gider. Geçerli klasörde çalıştırmak için Enter'a basın;
Shift+Enter onu açık kalan bir terminal penceresinde çalıştırır.

- `cd path` klasörü değiştirir; `md name` bir klasör oluşturur.
- Tab dosya ve klasör adlarını tamamlar.
- Ctrl+Enter imlecin altındaki adı komut satırına ekler, Ctrl+Shift+Enter tam yolu ekler.
- Satırın sonundaki ok (veya Alt+F8) son komutlarınızı listeler.
- Sondaki terminal düğmesi [dahili terminali](tools.md#built-in-terminal) panellerin altında açar (Ctrl+`
  onu kendi sekmesiyle bir sayfa olarak açar).

## Dışarıdan dosya açma

NextCommander'ı bir klasör ya da dosyayla başlatabilirsiniz: `NextCommander C:\Projects` veya
`NextCommander report.txt`. NextCommander zaten çalışıyorsa klasör çalışan pencerenin yeni bir sekmesinde açılır, dosya
ise klasöründe imleç üzerindeyken açılır. `NextCommander --lister file` yalnızca görüntüleyiciyi,
`NextCommander --edit file` yalnızca düzenleyiciyi açar.

## Yönetici hakları

Bazı dosyalar ve klasörler yalnızca yönetici (root) haklarıyla değiştirilebilir; örneğin program klasörleri ya da başka
bir kullanıcının dosyaları. Erişim reddedildiği için kopyalama, taşıma, silme, yeniden adlandırma ya da klasör
oluşturma başarısız olduğunda NextCommander yönetici haklarıyla yeniden denemeyi önerir. Kabul ederseniz sisteminiz
kendi izin iletişim kutusunu gösterir:

- **Windows:** Kullanıcı Hesabı Denetimi (UAC) istemi.
- **Linux:** masaüstünün parola istemi (polkit).
- **macOS:** sistemin yetkilendirme iletişim kutusu.

Yalnızca o tek işlem yüksek haklarla çalışır; NextCommander'ın kendisi sizin kullanıcınız olarak çalışmaya devam eder.

Bir düğme çubuğu düğmesinde, bir program adının önündeki `*` o programı yönetici olarak çalıştırır.

## Sonraki adımlar

- [Klavye kısayolları](keyboard-shortcuts.md)
- [Araçlar](tools.md)
- [Yapılandırma](configuration.md)
