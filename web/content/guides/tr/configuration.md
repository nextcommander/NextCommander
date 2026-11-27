# Yapılandırma

[README'ye dön](../../README.md)

NextCommander'ın ayarlarının çoğu Yapılandırma'dadır. *Yapılandırma > Seçenekler...* ile, Ctrl+Alt+S ile ya da
düğme çubuğunun sonundaki ayarlar düğmesiyle açın. Ana pencerenin içinde, iki panelin yerine, kendi sayfası olarak
açılır: panellerin üstünde, dosyalar sekmesi ve bir **Seçenekler** sekmesi bulunan bir sekme şeridi belirir; böylece
seçenekleri kapatmadan dosyalara dönebilir, sonra seçeneklere yeniden geçebilirsiniz. Tamam, İptal, Esc ya da
sekmenin `×` düğmesi onu kapatır. Başka bir pencereden açılırsa (örneğin paketleme penceresindeki *Yapılandır...*
ile) kendi penceresi olarak açılır.

![Yapılandırma penceresi, Düzen sayfası](../screenshots/configuration.png)

## Yolunuzu bulmak

- **Sayfalar:** soldaki liste sayfaları simgeleriyle gösterir. Görünüm, İşlem ve Paketleyici'nin alt sayfaları vardır
  (ok işareti bunları gösterir ya da gizler). Yapılandırma en son kullandığınız sayfada açılır.
- **Arama (Ctrl+F):** herhangi bir sayfadaki bir ayarı adıyla ya da açıklamasıyla bulmak için sayfaların üstündeki
  kutuya yazın. Aksanlar önemli değildir ve arama çevrilmiş metinlerle de çalışır. Sonuçlar sayfaya göre gruplanır ve
  orada değiştirilebilir; bir grubun başlığına tıklayarak sayfasını açın. Esc aramayı temizler.
- **Neredesiniz:** sayfa başlığının üstünde yerini görürsünüz, örneğin *Yapılandırma › Görünüm*.
- **Değiştirilen ayarlar:** sayfa başlığının altında sayfadaki kaç ayarın varsayılandan farklı olduğunu görürsünüz;
  bunların her birinin adından sonra bir nokta bulunur, sayfa listesinde de böyle ayarları olan sayfalar bir
  noktayla işaretlenir.
- **Sayfayı sıfırla:** geçerli sayfadaki her ayarı varsayılanına döndürür. Diğer değişiklikler gibi bu da yalnızca
  Uygula'ya ya da Tamam'a tıkladığınızda yazılır.
- **Satırlar olarak ayarlar:** bir sayfanın her grubu bir karttır; bir satırda ayarın adı ve yardım metni solda,
  denetimi sağda yer alır. Açık / kapalı ayarlar anahtardır.
- **Bağımlı seçenekler:** girintili satırlar üstlerindeki anahtara aittir ve anahtar kapalıyken soluk görünür.

## Tamam, Uygula ve İptal

Değişiklikler **Uygula**'ya (Yapılandırma açık kalır) ya da **Tamam**'a (uygular ve kapatır) tıklayana kadar
kullanılmaz; alttaki çubuk kaç değişikliğin henüz uygulanmadığını gösterir. **İptal** (ya da Esc) onları atar. Dil
de hemen değişir. Birkaç ayar yeniden başlatmadan sonra etkili olur; yardım metinleri bunu söyler.

## Listeler

Bazı ayarlar listedir: renk kuralları (*Görünüm > Renk*), dosya türüne göre editörler (*İşlem > Düzenle ve
görüntüle*), dahili ilişkilendirmeler (*İşlem > İlişkilendirmeler*), dosya türüne göre küçük resimlerin altındaki
alanlar (*İşlem > Küçük resimler*), yok sayma listesi ve Başlat menüsü (*Çeşitli*). Hepsi aynı biçimde düzenlenir:

- girdiler soldadır; **Ekle**, **Kopyala**, **Kaldır** ve **▲ ▼** (sıra önemlidir: eşleşen ilk kural geçerlidir),
- seçili girdinin alanları sağdadır: maskeler ve adlar metin olarak, renkler bir renk örneği ve hızlı renklerle,
  programlar bir **...** düğmesi ve hızlı seçeneklerle (Yerleşik, Sistem, Lister ...), onay kutuları ve listeler,
- Başlat menüsünde ayrıca **Ayraç ekle** vardır.

Her girdi siz yazdıkça denetlenir (örneğin bir renk kuralı bir maske ve geçerli renkler ister; bir ilişkilendirme
bir maske ve bir program ya da komut ister; bir Başlat menüsü öğesi bir komut ister). Hatalı bir girdi kırmızı
gösterilir ve altında neyin yanlış olduğunu söyleyen bir satır bulunur; **bir listede hata varken Tamam ve Uygula
kaydetmeyi reddeder**, böylece ayarlarınızı yanlışlıkla bozamazsınız.

*İşlem > Eklentiler*, kurulu eklentileri birer onay kutusuyla listeler: işareti kaldırılanlar bir sonraki başlatmadan
sonra yüklenmez. *Görünüm > Görünüm modları*, görünüm modlarını listeler; **Düzenle...** onların düzenleyicisini açar
(sütunlar, otomatik olarak bir moda geçen klasörler, renkler, girerken ve çıkarken çalışan komutlar).

## Düğme çubuğu düzenleyicisi

**Düzen** sayfasında *Düğme çubukları* grubu, yatay ve dikey çubuk için düğme çubuğu düzenleyicisini açar (ayrıca
*Yapılandırma > Düğme çubuğu...* ve *Yapılandırma > Düğme çubuğu 2 (dikey)...* ile ve bir çubuğa sağ tıklayarak da
ulaşılır).

- Üstte çubuğun bir önizlemesi bulunur; düzenlemek için bir düğmeye tıklayın, taşımak için sürükleyin (Ctrl+← / →
  ve Delete de orada çalışır).
- Önizlemenin üstündeki düğmeler bir düğme ya da ayraç ekler, seçili olanı sola ya da sağa taşır ve siler; simge
  boyutu (16 ile 48 piksel arası, her çubuk için ayrı) bunların yanında seçilir.
- Her düğme için, **Eylem** altında: komut (bir NextCommander komutu, bir program ya da bir klasör), parametreler ve
  başlangıç klasörü; **Görünüm** altında: simge (önizlemesiyle), simgenin yanında gösterilen kısa bir etiket ve
  araç ipucu. Bunların altında, Windows'ta, programın normal, simge durumunda ya da ekranı kaplamış başlayıp
  başlamayacağı ve **Yönetici olarak çalıştır** (komutun önüne `*` koymakla aynıdır).
- Windows'ta, birden çok simgesi olan bir dosya (exe, dll, icl) seçmek bunlardan birini seçmenizi sağlar.
- **Kaydet** değişiklikleri saklar, **İptal** çubuğu olduğu gibi bırakır. Tek bir düğmeyi değiştirmek (çubuğa sağ
  tıklayın) aynı alanları kendi iletişim penceresinde açar.

Bir programı eklemek için düğme çubuğuna bırakabilir ya da bir düğmeye sağ tıklayıp değiştirebilir, silebilir veya
taşıyabilirsiniz.

**Düğme çubuğu stili** (aynı grup) üstteki çubuğa bir görünüm verir: *Klasik* (birkaç satıra sarılan küçük
düğmeler), *Mica* (Windows 11: 6 px köşeler, açık olan bir anahtar altında bir çizgiyle yumuşak mavi dolgu alır),
*Graphite* (4 px köşeli, alçak ve sık bir çubuk; açıkken düz dolgu) ya da *Aurora* (yuvarlak düğmeli, yüzen,
yuvarlatılmış bir çubuk; açıkken mor). Yeni görünümler tek satır tutar: pencere çok dar olduğunda tüm gruplar
(ayraçlar arasındakiler) sağdan başlayarak **⋯** menüsüne geçer. Görünüm düğmeleri (kısa, ayrıntılı, küçük resimler)
orada tek bir bölümde durur. Her görünümde, bir şeyi açıp kapatan düğme (görünüm, dal görünümü, hızlı filtre, gizli
dosyalar, klasör ağacı, terminal, Hızlı görünüm) açık olup olmadığını gösterir ve araç ipucu adı, kısayolu, açık /
kapalı durumunu ve komutu verir. **Simgelerin yanında adlar** düğmelerin adlarını da gösterir.

Bir komutun adını bilmeniz hiç gerekmez: bir komutun verilebildiği her yerde (düğme çubuğu, kullanıcı komutları,
hotlist, görünüm modlarının komutları, dahili ilişkilendirmeler ve Başlat menüsü) **Komut...**, tüm komutları
simgeleri ve açıklamalarıyla listeler. Birini bulmak için bir adın ya da açıklamanın bir kısmını yazın, sonra çift
tıklayın ya da Enter'a basın. Yanındaki **...** bunun yerine bir program seçer.

## Sayfalar

| Sayfa | Neleri kapsar |
|---|---|
| **Düzen** | Pencerenin hangi bölümlerinin gösterileceği: düğme çubukları, sürücü düğmeleri (ve Ev, Belgeler, İndirilenler ve Masaüstü kısayolları), sürücü kutusu, klasör sekmeleri, yol satırı (tıklanabilir parçalar, üzerine gelince açma, geçmiş ve hotlist düğmeleri), sütun başlıkları, durum çubuğu, komut satırı, işlev tuşları, ağaç paneli, üst üste paneller ve düğme çubuklarının kendileri. |
| **Görünüm** | Gizli ve sistem dosyaları, sürücü köklerinde `[..]`, klasör adlarının çevresinde köşeli parantezler, sıralama yöntemi, klasörlerin nasıl sıralanacağı, klasör ağacı, araç ipuçları ve otomatik klasör boyutları. |
| Görünüm › **Simgeler** | [Simge paketi](icon-packs.md), dosya listesinin hangi simgeleri gösterdiği, kaplama simgeleri, soluk gizli dosyalar, simge boyutu ve aralığı, ana menüdeki simgeler, sürücü simgeleri. |
| Görünüm › **Yazı tipi** | Dosya listesi, ana pencere, iletişim pencereleri, Lister ve editör ile dahili terminal için yazı tipleri ve boyutları; iletişim penceresi ölçeklemesi. |
| Görünüm › **Renk** | [Renk teması](themes.md), kendi temanızı kaydetme, dosya türü renkleri ve kuralları, ters seçim. |
| Görünüm › **Veri biçimleri** | Boyut biçimi, tarih ve saat biçimi, ayraçlar, sütun genişlikleri, uzantı sütunu, üst ve alt bilgi. |
| Görünüm › **Klasör sekmeleri** | Sekmelerin nasıl göründüğü (birkaç satır, aynı genişlik, başlık uzunluğu, simgeler, kilitli sekmeler) ve nasıl davrandığı (Ctrl+Tab, yeni sekmelerin nerede açılacağı, çift tıklayarak kapatma, sekme başına filtre). |
| Görünüm › **Görünüm modları** | Özel sütun setleri ve görünüm modları, arka planları ve sekme renkleriyle. |
| Görünüm › **Dil** | Programın [dili](languages.md). |
| **İşlem** | Tek pencere, sistem tepsisi, başlık çubuğunda ilerleme, gezinme (tek tıklama, yol tamamlama, sürücü değişince köke gitme), fareyle seçim, Windows Gezgini bağlam menüsü, çıkışta nelerin kaydedileceği, dosya açıklamaları, güncellemeler. |
| İşlem › **Düzenle ve görüntüle** | Lister seçenekleri (sözcük kaydırma, önizlemeler, yeniden yükleme, boyut sınırı) ve editör (dahili ya da harici, dosya türüne göre editör). |
| İşlem › **Kopyala ve sil** | Geri Dönüşüm Kutusu, onaylar, kopyalamadan sonra doğrulama, sembolik bağlantılar, hedef varsa ne yapılacağı, sürükle ve bırakta Kopyala iletişim penceresi, kopyalama tamponları ve büyük dosya modu, kopyalama ve silme için Windows Gezgini'ni kullanma (Windows). |
| İşlem › **Hızlı arama** | Hızlı aramanın nasıl başladığı (Ctrl+Alt+harf ya da yalnızca harfler) ve nasıl eşleştiği (başta, herhangi bir yerde, bulanık). |
| İşlem › **FTP** | Gizli dosyalar, pasif mod, yeniden denemeler, aktarım türü (ikili / metin), FTP günlüğü, etkin mod portları. |
| İşlem › **Proxy** | Web isteklerinin (WebDAV, S3, Azure, bulut sürücüleri, URL indirmeleri, güncelleme denetimi) internete nasıl ulaştığı: sistemin proxy'si, hiçbiri, kendi proxy'niz (HTTP, HTTPS, SOCKS4 / 4a / 5 ya da bir SSH tüneli) kullanıcı, parola ve doğrudan bağlanılacak adreslerle, ya da otomatik yapılandırma betiği (PAC); bir düğme bağlantıyı dener. Bir bağlantının kendi proxy'si olabilir (Bağlantı yöneticisindeki Proxy sekmesi). Bkz. [Bağlantılar](connections.md#proxy). |
| İşlem › **Eklentiler** | Kurulu eklentiler; işareti kaldırılanlar yüklenmez. |
| İşlem › **Küçük resimler** | Küçük resim boyutu, disk önbelleği, küçük resimlerin altında gösterilen alanlar, metin dosyalarının ve diğer dosyaların küçük resimleri. |
| İşlem › **İlişkilendirmeler** | Dahili ilişkilendirmeler: Enter'ın bir dosya türü için ne yaptığı (bir program, Lister, editör, sistemin programı ya da bir komut). |
| İşlem › **Dosya indeksi** | [Dosya indeksi](tools.md#file-index): açık ya da kapalı, hangi klasörler, nelerin dışarıda bırakılacağı, durumu ve Yeniden oluştur. |
| İşlem › **Karşılaştır** | [İçeriğe göre karşılaştırma](tools.md#compare-by-content): nelerin yok sayıldığı (büyük / küçük harf, boşluklar, boş satırlar, satır sonları), taşınan satırlar, yan yana benzer satırlar, sözcüklere ya da karakterlere göre farklar, yazı tipi, satır filtreleri ve yerine koymalar (düzenli ifadeler), kaydederken yedek, harici bir karşılaştırma programı. |
| **Paketleyici** | Varsayılan arşiv biçimi, arşiv tarihini en yeni dosyasına ayarlama, Enter'ın arşivleri açıp açmayacağı. |
| Paketleyici › **Zip** | Sıkıştırma düzeyi, UTF-8 dosya adları, parola korumalı zip'lerin şifrelenmesi, geçici dosyalar, hasarlı ve kısmi zip'ler. |
| Paketleyici › **7-Zip** | 7-Zip'in nerede olduğu, sıkıştırma düzeyi ve yöntemi, katı arşivler, işlemci iş parçacıkları, dosya adlarını şifreleme, sözlük ve sözcük boyutu. |
| **Yenile** | Dosya listelerinin otomatik yenilenmesi (ağ ve çıkarılabilir sürücülerde de), dışarıda bırakılan klasörler, pencere odağı alınca yenileme. |
| **Yok sayma listesi** | Panellerde hiç gösterilmeyen dosya ve klasörler. |
| **Geçmiş** | Klasör ve komut satırı geçmişinin uzunluğu, sık kullanılan klasörler. |
| **Günlük dosyası** | Dosya işlemlerinin günlüğe yazılıp yazılmayacağı, günlüğün nereye gideceği, hangi işlemler, yeni günlük dosyası başlatma; NextCommander'ın kendi program günlüğünü yazıp yazmayacağı. |
| **Çeşitli** | Kapatmadan önce sorma, dahili terminal (kabuk, gerçek terminal), Başlat menüsü, onaylar, bir işlem bitince ses ve *Kısayollar ve komutlar...*, *Hotlist...*, *Sütun setleri...*, *Ana parola...*, *Ayarları dışa aktar...*, *Ayarları içe aktar...*, *Ayarları bir yedekten geri yükle...* ve *Ayar klasörünü aç* düğmeleri. |

| | |
|---|---|
| ![Simgeler sayfası](../screenshots/configuration-icons.png)<br>Görünüm › Simgeler | ![7-Zip sayfası](../screenshots/configuration-7zip.png)<br>Paketleyici › 7-Zip |

## Diğer yapılandırma komutları

Yapılandırma menüsü türe göre gruplanmıştır:

- **Ana ayarlar:** *Seçenekler...* ve **Eklentiler...** (eklentiler penceresi: kurulu eklentiler ve durumları,
  açıp kapatma, bir zip dosyasından kurma).
- **Görünüm:** **Renk teması** (hızlı tema değiştirme), **Koyu <-> Normal** (sistem renkleri ile Koyu tema arasında)
  ve **Sütun setlerini düzenle...**
- **Düğme çubukları:** *Düğme çubuğu...*, *Düğme çubuğu 2 (dikey)...* ve **Düğme çubuğuna düğme ekle...**
- **Komutlar ve listeler:** **Komutlar ve kısayollar...** (her komut menüsüne göre, arama, favoriler ve
  kısayollarıyla; kısayol atama ve kaldırma, bkz. [Klavye kısayolları](keyboard-shortcuts.md#changing-shortcuts)),
  **Kullanıcı komutu ekle (uc_)...** ve **Hotlist'i düzenle...**
- **Kaydetme ve yedekler:** **Konumu kaydet** ve **Ayarları kaydet** (pencere konumunu ve tüm ayarları şimdi
  kaydeder), ardından *Ayarları dışa aktar...*, *Ayarları içe aktar...* ve *Ayarları bir yedekten geri yükle...*

**Başlat** menüsü programlar ve komutlar için kendi girdilerinizi tutar; *Başlat > Başlat menüsünü değiştir...* onları
düzenler.

Ayrıca bkz.: [Ayarlar klasörü](settings-folder.md)
