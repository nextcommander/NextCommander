# Bağlantılar

[README'ye dön](../../README.md)

NextCommander sunucuları ve bulut depolamayı yerel dosyalarınızla aynı panellerde açar. Kopyalama, taşıma, silme,
yeniden adlandırma, görüntüleme (F3), düzenleme (F4) ve arşivleri açma burada da çalışır.

Desteklenen türler:

| Tür | Ne için |
|---|---|
| SFTP (SSH) | Linux ve Unix sunucuları, NAS cihazları |
| SCP (SFTP'siz SSH) | SFTP alt sistemi olmayan SSH sunucuları (yönlendiriciler, küçük cihazlar) |
| FTP / FTPS | Şifreli ya da şifresiz (açık veya örtük TLS) FTP sunucuları |
| WebDAV | Nextcloud, ownCloud, SharePoint, IIS, Apache, NAS cihazları |
| SMB (Windows paylaşımı) | Windows paylaşımları, Samba, NAS cihazları; Linux ve macOS'ta da bağlamadan |
| Amazon S3 / uyumlu | Amazon S3, MinIO, Cloudflare R2, Wasabi, Backblaze B2, DigitalOcean Spaces |
| Azure Blob Storage | Azure depolama hesapları |
| Azure Files | Bir Azure depolama hesabının dosya paylaşımları |
| Google Drive | Drive'ınız, "Benimle paylaşılanlar" ve paylaşılan sürücüler |
| OneDrive | Kişisel ve iş / okul OneDrive, isteğe bağlı olarak SharePoint siteleri |
| Dropbox | Dropbox'ınız |
| Box | Box hesabınız |
| Web sunucusu klasör listeleri | Dizin sayfası olan `http://` ve `https://` klasörleri (Apache, nginx, IIS ...); salt okunur |
| ADB üzerinden Android | USB hata ayıklaması açık telefonlar (MTP yokken de) |
| PC bağlantısı | NextCommander çalıştıran başka bir bilgisayar; USB veya Ethernet kablosuyla ya da ağ üzerinden: bkz. [PC bağlantısı](#pc-link) |
| rclone uzak depoları | `rclone config` ile kurduğunuz her uzak depo (70'ten fazla hizmet); programla birlikte gelen rclone eklentisi |
| Uzak masaüstü (RDP) | Bir Windows bilgisayarın (ya da xrdp'li bir Linux'un) masaüstü; NextCommander'ın bir sekmesinde veya sistemin RDP istemcisinde: bkz. [Uzak oturumlar](#remote-sessions-rdp-vnc-ssh-telnet-rsh-serial-port) |
| VNC | Başka bir bilgisayarın ekranı (VNC sunucusu olan her sistem), bir VNC görüntüleyicisinde |
| SSH | Bir sunucudaki kabuk, dahili terminalde |
| Telnet | Dahili terminalde metin oturumu (yönlendiriciler, anahtarlar, eski sistemler) |
| rsh / rlogin | SSH'sız eski Unix sunucuları, dahili terminalde (şifresiz) |
| Seri port | Bir anahtarın, yönlendiricinin, sunucunun veya kartın seri kablo üzerinden konsolu (COM3, /dev/ttyUSB0), dahili terminalde |

## Bağlantı menüsü

- **Bağlantı yöneticisi...** (Ctrl+F): kayıtlı bağlantılarınız.
- **Kayıtlı bağlantılar:** kayıtlı bir bağlantıyı doğrudan açar; bağlantılar türe göre gruplanır (SFTP, FTP,
  Amazon S3, Google Drive ...).
- **Adresle bağlan...** (Ctrl+N): bir adres yazın ve kaydetmeden bağlanın.
- **Bağlantıyı kes** (Ctrl+Shift+F): etkin panelin bağlantısını kapatır; panel son yerel klasöre döner. Bir panel
  bir bağlantıyı gösterirken yol satırında bağlantının adını taşıyan kırmızı bir düğme vardır; aynı işi yapar.
- **Ağ (yerel ağdaki makineler)**: bkz. [Ağ komşuluğu](#network-neighborhood).
- **Ağ sürücüsü bağla...**, **Ağ sürücülerinin bağlantısını kes...**, **Geçerli klasörü paylaş...**,
  **Paylaşımı kaldır...**: Windows'un ağ sürücüsü ve paylaşım pencereleri (Windows).
- **URL'den indir...** ve **Listedeki adresleri indir...**: web'den dosya indirir.
- **FTP'de gizli dosyaları göster**.
- **Ana parola...**: bkz. [Kayıtlı parolalar](#saved-passwords-and-the-master-password).
- **PC bağlantısı...**, **PC bağlantısıyla paylaş**: bkz. [PC bağlantısı](#pc-link).
- **SSH tünelleri...**: bkz. [SSH tünelleri](#ssh-tunnels).
- **Ağ araçları...**: **Ping** (yanıtlar süreleri ve TTL ile, ardından gönderilen / alınan / kaybolan ve en az / ortalama /
  en çok), **Port tarama** (bir sunucunun TCP portları: `22, 80, 8000-8100` gibi bir liste, *Yaygın portlar* ya da 1-1024;
  açık olanlar, bulundukça olağan hizmetleriyle) ve **Wake-on-LAN** (bir MAC adresine sihirli paket, yayın olarak
  gönderilir). Etkin panelin bağlantısının sunucusu hazır doldurulur.

Düğme çubuğunda bağlantı yöneticisinin düğmesi, kayıtlı bağlantı yokken yöneticiyi açar; bağlantı olunca açılır bir
menüdür (▾): önce **Bağlantı yöneticisi...**, ardından türe göre gruplanmış kayıtlı bağlantılar (`nc_ConnectionsMenu`
komutu aynı menüyü açar).

Yanındaki **Eklentiler** düğmesi (`nc_PluginsMenu`) eklentilerin sunduğu yerleri (rclone uzak depoları gibi) ve eklenti
listesini gösterir. Yalnızca eklentiler yüklüyken görünür; *Yapılandırma > Seçenekler > Düzen > Düğme çubukları > Eklentiler düğmesi*
onu kapatır.

## Bağlantı yöneticisi

![Bağlantı yöneticisi](../screenshots/connections.png)

Yönetici, dahili terminal gibi, sekmeli ayrı bir sayfa olarak açılır: üstündeki **Dosyalar** sekmesi yönetici açık
kalırken panellere döner; ✕, sekmesinin × düğmesi veya Esc onu kapatır.

Soldaki **kenar çubuğu**: **＋ Yeni bağlantı** (Ctrl+N) ve yanındaki **⋯** (yeni klasör, başka bir programdan içe
aktarma, dışa aktarma), **arama** (Ctrl+F; adlarda, sunucularda, kullanıcılarda ve klasörlerde arar) ve listeyi
süzen **çipler**: *Tümü*, *★ Sık kullanılanlar*, *● Bağlı* ve dört bağlantı kategorisi: *Sunucular*, *Ağ* (yerel ağ),
*Bulut* (depolama ve sürücüler) ve *Uzak* (uzak oturumlar); her birinde sayısı vardır, bağlantısı olmayan çip
gösterilmez. Altlarında **Genel bakış** satırı, sonra klasörler ve bağlantılar gelir. Her bağlantının, kategorisinin
renginde türünün karosu vardır; bağlıyken yeşil bir nokta görünür; adresi adının altındadır. Kırmızı bir **!**
henüz bağlanamayan bir bağlantıyı gösterir (ipucu nedenini söyler), kırmızı nokta son denemesi başarısız olanı, ★ sık
kullanılanı. En alttaki satır bunları sayar ("12 kayıtlı · ● 2 bağlı").

Sayfanın üstündeki **konum çubuğu**: **←** ve **→** (Alt+Sol, Alt+Sağ, farenin geri ve ileri düğmeleri) bir
tarayıcı gibi görülen sayfalar arasında gezdirir; sonrasındaki yol (*Genel bakış* › klasörler › bağlantı) bir
tıklamayla herhangi birine geri götürür; Alt+Home genel bakışa gider. Odak yöneticinin sayfasında olduğu sürece
tuşlar her yerde çalışır.

![Bağlantı yöneticisinin genel bakışı](../screenshots/connections-overview.png)

**Genel bakış** (hiçbir şey seçili değilken; yönetici de onunla açılır): **son kullanılan** bağlantılar kartlar halinde
(tıklamak bağlanır; adın altında en son ne zaman kullanıldığı yazar) ve **bir adrese bağlanmak** için bir kutu. İlk
seferde, bağlantı yokken, bunun yerine nasıl başlanacağını gösterir: yapıştırılacak bir adres, yaygın türler (SFTP,
FTPS, SMB, Amazon S3, OneDrive), *Tüm türleri gör* ve FileZilla, WinSCP, PuTTY, MobaXterm ve bir bağlantı dosyası için
içe aktarma düğmesi olan **Başka bir programdan mı geçiyorsunuz?**.

Sağdaki **bir bağlantı**: karosu, **adı** (yerinde yeniden adlandırmak için tıklayın, ✎ ya da F2; Enter yeni adı, Esc
eskisini korur), adresi ve türü (ya da ● Bağlı). Başlığın sağında **★** (sık kullanılan), **⋯** menüsü (yeniden
adlandır, çoğalt, adresi kopyala, uzak masaüstü için *Dosyalar (SMB)*, SSH için *Dosyalar (SFTP)*, kayıtlı parolayı
unut, sil), **Dene** ve **Bağlan** (bağlıyken *Panelde aç*). Başlığın altında renkli satırlarla notlar: son denemenin
sonucu, parolayı okunabilir metin olarak gönderen türler için bir uyarı (FTP, HTTP üzerinden WebDAV, Telnet, rsh),
türün kendi notu. Sonra düzenleyicinin sekmeleri gelir. Bir değişiklik, altta *Geri al* ve *Kaydet* (Ctrl+S) ile
**Kaydedilmemiş değişiklikler** gösterir ve konum çubuğunda addan sonra bir • belirir.

Sağdaki **bir klasör**: adı (o da yerinde yeniden adlandırılır), kaç bağlantısı ve alt klasörü olduğu, *Yeni
bağlantı*, *Yeni alt klasör*, *Sil* ve *Tümüne bağlan*, renk etiketi ve içeriği kartlar halinde.

- **Klasörler** iç içe olabilir ve renk etiketi taşır (örneğin üretim için kırmızı). Bağlantıları ve klasörleri
  taşımak için sürükleyin: bir klasörün üzerine (vurgulanır) bırakmak içine koyar, bir bağlantının üzerine bırakmak o
  bağlantının klasörüne koyar, ağacın altındaki boş alana bırakmak en üste koyar; bir klasör kendi içine giremez.
  Taşıma hemen kaydedilir. Boş bir klasör siz silene kadar kalır.
- **Tuşlar:** oklar gezdirir ve klasörleri açar (aramadan Aşağı listeye girer), Enter bağlanır (Shift+Enter: sağ
  panelde), F2 yeniden adlandırır, Delete siler (bir klasörü içindekilerle birlikte; kayıtlı parolaları unutulur),
  Ctrl+Z son silinenleri geri koyar, Ctrl+D çoğaltır, Ctrl+N yeni bağlantı yapar, Ctrl+S kaydeder. Listenin sağ tık
  menüsünde aynıları, ayrıca sık kullanılanlar, adresi kopyalama ve kayıtlı parolayı unutma vardır.
- **Yeni bağlantı:** **＋ Yeni bağlantı** (Ctrl+N, sağ tık menüsü, *Bağlantı > Yeni bağlantı*, genel bakıştaki bir tür
  kartı, oraya yapıştırılan bir adres) yeni bağlantının sayfasını düzenleyicinin yerinde açar; üstünde gösterilen iki
  adımla (1 Tür › 2 Bilgiler; ✓ 1 türlere geri döner).

  ![Yeni bir SFTP bağlantısının bilgileri](../screenshots/connections-new.png)

  - **Tür:** her türün kartları, gruplar halinde (*Sunucular*, *Yerel ağ*, *Bulut depolama*, *Uzak oturumlar*;
    yaygın olanlar *Yaygın* ile işaretlidir). **Arama kutusu**, adında, açıklamasında ya da grubunda yazılan tüm
    kelimeler geçen türleri bırakır (`cloud`, `azure`, `windows` ...; tek tür kalınca Enter onu seçer). Yanına
    `sftp://user@server:22/folder` gibi bir adres yapıştırılabilir: kutunun altında ne bulduğunu söyler ("server
    sunucusuna SFTP bağlantısı — devam etmek için Enter'a basın"), Enter türü, sunucuyu, portu, kullanıcıyı ve başlangıç
    klasörünü doldurur. Bir türün *Bağlantı > Yeni bağlantı* seçeneği bilgiler adımından başlar.
  - **Bilgiler:** başlık türü adlandırır (*Türü değiştir* geri döner). Bir not, bilgilerin bir adresten doldurulduğunu,
    türün parolayı okunabilir metin olarak gönderdiğini ve türün kendi notunu söyler. **Ad** kartı ilk gelir: ad
    önerilir (*Önerilen*: bilindiklerinde user@server, yoksa tür, o alınmışsa "SFTP 2") ve siz kendi adınızı yazana
    kadar bilgiler yazıldıkça onu izler; boşsa öneriyle kaydedilir. **Klasör** türün grubudur (başka bir klasör
    yazılabilir, yeniyse oluşturulur; bir klasörün *Yeni bağlantı* düğmesi onu oraya koyar). Sonra yalnızca türün
    ihtiyaç duyduğu şeyler, *Bağlantı* (ya da *Cihaz*) ve *Giriş* kartlarında: sunucu ve port, isteğe bağlı bir
    başlangıç klasörü, kullanıcı (SSH için zorunlu, S3'te erişim anahtarı kimliği, Azure'da depolama hesabı), varsa
    paylaşılan bir giriş, isteğe bağlı bir anahtar dosyası (SFTP / SCP / SSH), bir bulut sürücüsünün uygulama kimliği
    (ve Box'ın istemci sırrı), seri port. Zorunlu alanlarda kırmızı bir * vardır. Parola, her bağlantıda olduğu gibi
    bağlanırken sorulur.
  - **Diğer ayarlar** (kapalı) düzenleyicinin kendi Gelişmiş sekmesini, bir uzak masaüstünün Görünüm / Yerel kaynaklar /
    Deneyim sekmelerini ve Proxy sekmesini içerir: zaman aşımları, bir atlama sunucusu, şifreleme, proxy ... (hepsi
    sonradan düzenleyicide de değiştirilebilir). Bir sorun içindeyse kendiliğinden açılır.
  - Alttaki çubuk: **Bağlantıyı dene** (sonucu yanında görünür), **İptal** (Esc; hiçbir şey eklenmez), **Kaydet**
    (bağlantıyı klasörüne ekler, listede gösterir ve kaydeder) ve **Kaydet ve bağlan**. Kaydet ve Dene, eksik ya da
    yanlış olanı önce kendi alanında işaretler. Alt+Sol bir adım geri gider.

  Geri kalan her şey sağdaki düzenleyicide, sekmelerinde:
  - **Genel:** sunucu, port ve başlangıç klasörü (FTP için ayrıca şifreleme ve pasif mod, S3 için bölge; S3 ve WebDAV
    için bir **Hizmet**, aşağıya bakın).
  - **Giriş:** yöntem: parola, anahtar dosyası ya da **SSH agent** (SFTP / SCP); erişim anahtarı ya da **AWS profili /
    SSO** (S3); hesap anahtarı / SAS ya da **Microsoft Entra ID** (Azure); anonim (FTP). Bulut sürücüleri uygulama
    kimliklerini burada alır.
  - **Gelişmiş:** zaman aşımı, eşzamanlı bağlantılar, bir **atlama sunucusu** (SFTP / SCP), **dosya adı kodlaması**
    (FTP), SSE-S3 veya SSE-KMS ile **sunucu tarafı şifreleme** (S3), düz HTTP, her sertifikayı kabul etme, SharePoint
    siteleri (OneDrive) ve sık kullanılan işareti.
  - **Günlük:** bağlantının kendi günlüğü (aşağıdaki *Bağlantı günlükleri* bölümüne bakın).
- **Doldurulması gerekenlerin** başlığından sonra kırmızı bir \* vardır: sunucu; SFTP, SCP ve SSH'ın kullanıcısı (bir
  SSH girişinin her zaman vardır); S3'ün erişim anahtarı kimliği (ya da bir AWS profili) ve Azure'un depolama hesabı;
  bir bulut sürücüsünün uygulama kimliği (ve Box'ın istemci sırrı); giriş anahtar dosyasıyla yapılıyorsa anahtar dosyası
  (var olmalı); kendi proxy'sinin sunucusu. Boş ya da yanlış bir alan kırmızı çerçeveyle, altında neyin yanlış olduğu
  yazılı olarak gösterilir (yeni bağlantıda doldurmaya başlayınca). *Kaydet*, *Bağlan* ve *Dene* böyle bir bağlantıyı
  reddeder, ilk sorunun sekmesini gösterir ve imleci oraya koyar; ağaçta kırmızı bir **!** henüz bağlanamayan bir
  bağlantıyı işaretler (ipucu nedenini söyler).
- **Dene**, ayarlarla olduğu gibi bağlanır ve başlangıç klasörünü listeler; sonuç başlığın altındaki bir satırda
  görünür. **Kaydet** bağlantıları yazar (yöneticiyi kapatmak da yazar).
  **Bağlan** bağlantıyı açar; bir klasörün **Tümüne bağlan** düğmesi bağlantılarının her birini bir sekmede açar.
- **İçe aktar** (*＋ Yeni bağlantı* yanındaki **⋯** ya da ilk açılıştaki düğmeler) **FileZilla** (sitemanager.xml) ve
  **WinSCP** (WinSCP.ini ya da kayıt defteri anahtarı) sitelerini klasörleri, anahtar dosyaları, atlama sunucuları
  (WinSCP tünelleri) ve kayıtlı parolalarıyla (şifreli depoya) getirir (FileZilla'nın ana parolayla şifrelenmiş olanları
  okunamaz).
  Aynı menü **PuTTY** oturumlarını (Windows: kayıt defteri anahtarı; Linux / macOS: `~/.putty/sessions`): SSH,
  Telnet, rlogin ve seri portları kullanıcı, anahtar dosyası, uzak komut, terminal türü ve proxy ile, ve bir
  `.mxtsessions` dosyasından ya da `MobaXterm.ini`'den **MobaXterm** oturumlarını klasörleriyle içe aktarır: SSH
  (anahtar dosyası, ilk SSH ağ geçidi atlama sunucusu olarak, proxy, komut), SFTP, RDP (sürücüler, yazıcılar, pano, ağ
  geçidi ...), VNC, Telnet, rsh ve FTP. İkisi de içe aktarılabilecek parola tutmaz; yerel kabuklar, WSL, X11 ve diğer
  MobaXterm türleri dışarıda bırakılır.
- **Tüm bağlantıları dosyaya aktar...** (ya da bir klasör seçiliyken **Bu klasörü dosyaya aktar...**) bağlantıları
  klasörleriyle birlikte başka bir bilgisayar ya da kişi için bir dosyaya yazar: parolasız ve istemci sırrı olmadan.
  **NextCommander bağlantı dosyası...** böyle bir dosyayı içe aktarır; klasörde zaten alınmış bir ad numara alır
  ("web (2)").

S3 ve WebDAV için **Hizmet** listesi, bilinen hizmetlerin sunucusunu, portunu, bölgesini ve başlangıç klasörünü doldurur:
Amazon S3, Google Cloud Storage, Cloudflare R2, Backblaze B2, Wasabi, DigitalOcean Spaces, Hetzner ve MinIO;
Nextcloud / ownCloud, Yandex Disk, pCloud ve Koofr. Sunucu adının büyük harfli bir kısmı (`ACCOUNT_ID` ya da
`REGION` gibi) sizin değiştireceğiniz yerdir; alanların altındaki not nasıl yapılacağını söyler.

## Bağlantı günlükleri

Her bağlantının kendi günlüğü vardır: bağlanma (adres, atlama sunucusu, proxy, giriş yöntemi), giriş yapma,
yeni ya da değişmiş bir sunucu anahtarı, biten oturumlar, denemeler ve **her hata nedenleriyle birlikte** (sistemin
soket hatası gibi iç hatalar ve bir rapor için ayrıntılar). Parolalar, anahtarlar ve belirteçler asla yazılmaz.
Günlükler ayarlar klasörünün `logs/connections` klasöründeki dosyalardır (bağlantı başına bir tane, onun adıyla; 1 MB'ı
aşan `.old.log` olarak ayrılır); hatalar program günlüğü açıksa oraya da gider.

- Bağlantı yöneticisinde bir bağlantının **Günlük** sekmesi günlüğünü, hataları kırmızıyla gösterir ve yeni
  kayıtları izler; yanında *Yenile*, *Kopyala* ve *Günlüğü temizle* vardır.
- Bir sunucu açılamadığında ileti bağlantıyı (ad ve adres), nerede olduğunu, hatayı nedenleriyle ve günlük dosyasını
  belirtir; **Günlüğü göster** günlüğü Lister'da açar. *Dene* de nedenlerin tüm zincirini gösterir.
- Terminal oturumları (SSH, Telnet, rsh, seri port) nedenlerini ve günlük dosyasını terminale yazar; bir uzak
  masaüstü sekmesi bitiş iletisinin altına yazar.

## Adres yazma

Bir adresi doğrudan yol satırına (Alt+D) ya da *Adresle bağlan...* penceresine de yazabilirsiniz:

| Tür | Örnek |
|---|---|
| SFTP | `sftp://user@server.example.com:2222/home/user` |
| FTP | `ftp://user@ftp.example.com/` |
| WebDAV | `davs://user@cloud.example.com/remote.php/dav/files/user` (şifresiz için `dav://`) |
| SMB | `smb://DOMAIN;user@server/share` |
| S3 | `s3://ACCESS_KEY@s3.eu-central-1.amazonaws.com/bucket/folder` |
| SCP | `scp://user@router.example.com/etc` |
| Azure Blob | `azblob://account@account.blob.core.windows.net/container` |
| Azure Files | `azfile://account@account.file.core.windows.net/share/folder` |
| Web sunucusu klasörü | `https://archive.apache.org/dist/` |
| ADB üzerinden Android | `adb://SERIAL/sdcard/DCIM` |
| rclone | `rclone://remote/folder` (`rclone:///` uzak depoları listeler) |
| RDP, Telnet | `rdp://user@pc.example.com`, `telnet://router:2323` (*Adresle bağlan...* içinde; panelde açılmazlar) |

## Türlere göre notlar

### SFTP
Bir parolayla, özel anahtar dosyasıyla (kendi parolası olabilir) ya da **SSH agent**'ın anahtarlarıyla giriş yapın:
Windows OpenSSH agent'ı, PuTTY'nin Pageant'ı ya da Linux ve macOS'ta `SSH_AUTH_SOCK` agent'ı. Anahtarlar agent'ta
kalır, girişi o imzalar. Bir **atlama sunucusuyla** (`user@bastion:22`) bağlantı o SSH sunucusu üzerinden geçer;
aynı anahtar dosyası, agent ya da parolayla giriş yapar ve anahtarı bir sunucununki gibi onaylanır.

### SCP
SFTP alt sistemi olmayan SSH sunucuları içindir. Giriş (parola ya da anahtar dosyası) SFTP ile aynıdır. Klasörler
sunucuda kabuk komutlarıyla (`stat`, `mkdir`, `mv`, `rm`, `touch`) listelenir ve değiştirilir, bu yüzden sunucunun
bir POSIX kabuğu olmalıdır; dosyalar SCP ile gönderilir.

### FTP / FTPS
Düz FTP, açık TLS'li FTPS ya da örtük TLS'li FTPS (port 990) ve pasif ya da etkin mod seçin. Dosya adı kodlaması,
sunucu destekliyorsa UTF-8'dir; eski sunucular için birini seçin (Windows-1254, ISO-8859-9 ...).
Daha fazla FTP seçeneği (aktarım türü, yeniden denemeler, günlük dosyası) [Yapılandırma penceresinin](configuration.md)
*İşlem > FTP* sayfasındadır.

### WebDAV
Başlangıç klasörü sunucunun WebDAV yoludur, örneğin Nextcloud için `/remote.php/dav/files/<user>`.
Kullanıcı adı olmadan bağlantı anonimdir. Sunucu izin veriyorsa kopyalarken dosya zamanları korunur.

### SMB
Kullanıcı `DOMAIN\user` ya da yalnızca `user`'dır; konuk girişi için boş bırakın. Kök, makinenin
paylaşımlarını listeler.

### Amazon S3 ve uyumlu hizmetler
- **Sunucu:** S3 uç noktası, örneğin `s3.eu-central-1.amazonaws.com` ya da kendi MinIO sunucunuz.
- **Kullanıcı:** erişim anahtarı kimliği. Gizli anahtar parola olarak sorulur ve kaydedilebilir.
- Ya da bir **AWS profili** seçin: anahtarlar o zaman AWS komut satırı ayarlarınızdan okunur ve parola sorulmaz.
- Kök, bucket'larınızı listeler; orada F7 bir bucket oluşturur. Klasörler nesne adlarındaki `/` ile oluşur.
- Seçenekler: bölge (genellikle kendiliğinden bulunur), sanal barındırmalı adresler ve yerel bir MinIO için "TLS yok".
- **AWS SSO:** IAM Identity Center için kurulmuş bir AWS profili (~/.aws/config içinde `sso_session` ya da
  `sso_start_url`) hesabının ve rolünün geçici anahtarlarını verir. Oturum açma belirteci AWS CLI ile paylaşılır; süresi
  dolduğunda tarayıcıda oturum açılır (`aws sso login` gibi).
- **Sunucu tarafı şifreleme** (Gelişmiş): yüklenen her nesne için SSE-S3 ya da SSE-KMS (isteğe bağlı bir KMS anahtarıyla).

### Azure Blob Storage
Kullanıcı depolama hesabıdır; parola hesap anahtarı ya da bir SAS belirtecidir. Kök, kapsayıcıları listeler.
**Microsoft Entra ID** ile (Giriş sekmesi) bunun yerine iş hesabınızla tarayıcıda oturum açarsınız; hesabın
depolamada bir veri rolü olmalıdır (Storage Blob Data Reader / Contributor ...). Kiracı ve uygulama kimliği
boş kalabilir (kuruluşunuz; Azure CLI'nin uygulaması). Aynısı Azure Files için de geçerlidir.

### Azure Files
Kullanıcı depolama hesabıdır; parola hesap anahtarı ya da bir SAS belirtecidir. Kök, dosya paylaşımlarını listeler
(orada F7 bir paylaşım oluşturur). Bir paylaşım içindeki taşımalar sunucuda yeniden adlandırmadır; dosya zamanları korunur.

### Google Drive, OneDrive, Dropbox ve Box
- İlk bağlanışınızda tarayıcınız hizmetin kendi oturum açma sayfasını açar. **Girişi hatırla** açıkken yalnızca bir
  kez oturum açarsınız.
- **Hesap adı** yalnızca bir etikettir (örneğin `personal` ya da `work`); aynı hizmetin birkaç hesabını ayırt etmek içindir.
- **Kendi uygulama kimliğinize ihtiyacınız var.** NextCommander'ın kendi kimliği yoktur; bu yüzden hizmette bir kez
  ücretsiz bir uygulama kaydeder ve kimliğini bağlantıya girersiniz. Bağlantı penceresi adımları gösterir:
  - **Google Drive:** Google Cloud konsolunda bir proje oluşturun, Google Drive API'yi etkinleştirin, OAuth onay
    ekranını kurun (kendi hesabınızı test kullanıcısı olarak ekleyin) ve *Masaüstü uygulaması* türünde bir OAuth
    istemci kimliği oluşturun. İstemci kimliğini ve istemci sırrını girin.
  - **OneDrive:** Microsoft Entra'da kişisel ve kurumsal hesaplar için bir uygulama kaydı oluşturun,
    yönlendirme adresi `http://localhost` olan *Mobil ve masaüstü uygulamaları* platformunu ekleyin ve ona
    `Files.ReadWrite` ve `offline_access` izinlerini verin (SharePoint için ayrıca `Files.ReadWrite.All` ve
    `Sites.Read.All`). Uygulama (istemci) kimliğini girin.
  - **Dropbox:** Dropbox App Console'da *Scoped access* ve *Full Dropbox* ile bir uygulama oluşturun,
    `files.metadata.read/write` ve `files.content.read/write` izinlerine izin verin ve yönlendirme adresi olarak
    `http://localhost:53682/` ekleyin. Uygulama anahtarını girin.
  - **Box:** Box geliştirici konsolunda *User Authentication (OAuth 2.0)* ile bir *Platform App* oluşturun,
    yönlendirme adresi olarak `http://localhost:53682/` ve tüm dosya ve klasörleri okuma ve yazma kapsamını ekleyin.
    İstemci kimliğini ve istemci sırrını girin.
- Silinen dosyalar hizmetin kendi çöp kutusuna gider.
- **Google Drive:** kök, Drive'ım ile birlikte **Benimle paylaşılanlar** ve **Paylaşılan sürücüler** klasörlerini gösterir.
  Google Dokümanlar, E-Tablolar ve Slaytlar `.docx`, `.xlsx` ve `.pptx` dosyaları (Çizimler `.pdf`) olarak görünür ve
  kopyalarken ya da görüntülerken dönüştürülür; salt okunurdur.
- **OneDrive:** *SharePoint sitelerini de göster* ile (iş ya da okul hesapları) kökteki bir `SharePoint` klasörü
  kuruluşunuzun sitelerini ve belge kitaplıklarını listeler.

- **Dropbox ve Box** bir dosyanın zamanını yalnızca yükleme sırasında alır; kopya onu o zaman gönderir, böylece zamanlar korunur.

### Web sunucusu klasör listeleri
Sunucunun bağlantı listesi olarak gösterdiği bir klasörün `http://` ya da `https://` adresi (Apache, nginx, IIS,
lighttpd, Python'un http.server'ı ...) bir klasör gibi açılır. Boyutlar ve tarihler sayfada gösteriliyorsa oradan okunur.
Liste salt okunurdur: dosyaları oradan dışarı kopyalayın.

### ADB üzerinden Android telefonlar
*USB hata ayıklaması* açıkken (Geliştirici seçenekleri) ve kurulu `adb` ile (Android SDK platform-tools; PATH'te,
SDK klasöründe ya da `FM_ADB` ortam değişkeniyle bulunur) bağlı telefonlar, MTP olanların yanında "*Model* (ADB)"
adlı sürücüler olarak görünür. `/sdcard` (paylaşılan depolama) konumunda açılırlar; yukarı çıkmak Android'in izin
verdiği ölçüde telefonun geri kalanını gösterir. İlk seferde telefonda bilgisayara izin verin. Kopyalar dosya
zamanlarını korur; panelin alt bilgisi boş alanı gösterir.

### PC bağlantısı

NextCommander çalıştıran iki bilgisayar, eski dosya yöneticilerinin "port bağlantısı" gibi bir kablo üzerinden ya da
ağ üzerinden birbirlerinin klasörlerinde çalışabilir.

**Kablo.** İşletim sisteminin ağ bağlantısı olarak gösterdiği her şey işe yarar:
- iki bilgisayar arasında doğrudan bir **Ethernet** kablosu (yönlendirici gerekmez: ikisi de kendiliğinden
  169.254.x.x gibi bir adres alır; takıldıktan sonra bu bir dakikaya kadar sürer);
- iki bilgisayar arasında **USB-C / Thunderbolt** (Windows ve macOS'ta Thunderbolt ağı);
- sürücüsü bir ağ bağdaştırıcısı oluşturan **USB bağlantı kablosu** ("USB 3.0 bridge" / "data transfer" kablosu);
- iki bilgisayarın aynı Wi-Fi ya da LAN üzerinde olması.

Yalnızca kendi programlarıyla çalışan (ağ bağdaştırıcısı olmayan) eski aktarım kabloları desteklenmez.

**Paylaşan bilgisayarda**
1. *Yapılandırma > İşlem > PC bağlantısı*: **paylaşılan klasörleri** ekleyin (diğer bilgisayarın gördüğü bir ad, klasör
   ve diğer bilgisayar değiştiremesin diye *Salt okunur*). Bu klasörlerin dışındaki hiçbir şeye erişilemez.
2. **Bağlantı > PC bağlantısıyla paylaş** paylaşımı açar (bir onay işareti); aynı anahtar yukarıdaki sayfada da vardır,
   orada bilgisayar adı, port (47290) ve bu bilgisayarın adresleri de gösterilir.
3. İlk seferde Windows, NextCommander'ın ağı kullanıp kullanamayacağını sorar: izin verin. Doğrudan bir kablo
   Windows'ta çoğunlukla "Genel" ağdır; genel ağlara da izin verin ya da o ağı "Özel" yapın.

**Bağlanan bilgisayarda**
1. **Bağlantı > PC bağlantısı...** paylaşan bilgisayarları listeler (bir iki saniye içinde yanıt verirler). Birini
   açın: paylaşılan klasörleri görünür ve her şey yerel bir diskteki gibi çalışır — kopyalama, taşıma, silme,
   görüntüleme, düzenleme, dosya zamanları.
2. İlk seferde iki ekran da **aynı altı haneli kodu** gösterir: eşleşiyorsa burada kabul edin, diğer bilgisayarın
   kullanıcısı **İzin ver**'e tıklar. İki bilgisayar birbirini hatırlar; sonraki bağlantı hiçbir şey sormaz.
   *Yapılandırma > İşlem > PC bağlantısı > İzin verilen bilgisayarlar* bir bilgisayarı unutturur.
3. Listede görünmeyen bir bilgisayar elle yazılabilir: `link://169.254.12.34/` (adresi, ayar sayfasında gösterilir)
   ya da bağlantı yöneticisinde bir **PC bağlantısı** bağlantısı olarak kaydedilebilir.

**Bir bilgisayar bulunamadığında.** Arama, ağın 47290 portuna giden bir UDP iletisidir (yanıt aynı yoldan döner),
dosyalar TCP 47290 portundan geçer. Her iki bilgisayardaki bir güvenlik duvarı bunları düşürebilir:
- **Linux:** ufw açıksa PC bağlantısını kullanan her iki bilgisayarda portu açın: `sudo ufw allow 47290/udp` ve
  `sudo ufw allow 47290/tcp` (bir aramanın yanıtı güvenlik duvarı için yeni bir pakettir, bu yüzden arayan bilgisayarın
  da UDP portunu açması gerekir). firewalld: `sudo firewall-cmd --permanent --add-port=47290/udp --add-port=47290/tcp`
  ardından `sudo firewall-cmd --reload`.
- **Windows:** ağ "Özel" olmalı ya da NextCommander genel ağlarda izinli olmalıdır (*Windows Güvenliği >
  Güvenlik Duvarı > Bir uygulamaya izin ver*).
- **Güvenlik duvarında izin ver** (*Yapılandırma > İşlem > PC bağlantısı*) bunu çalıştığı bilgisayarda sizin için yapar:
  Windows Defender Güvenlik Duvarı port için gelen kurallar alır (TCP ve UDP, her ağ), Linux ufw ya da firewalld
  (biri açıksa) portu açar. Yönetici hakları ister; her iki bilgisayarda yapın.
- Her arama, ayarlar klasöründeki `logs/connections/PC link.log` günlüğüne yazılır: hangi adreslerden çıktığı ve
  bir şeyin yanıt verip vermediği; paylaşan bilgisayar oraya hangi aramaların kendisine ulaştığını yazar. Diğer
  bilgisayara ulaşan ama hiçbir şey bulamayan bir arama, dönüş yolunda düşürülmüştür.
- Adresi yazmak aramayı atlar (o zaman yalnızca TCP 47290 gerekir).

**Hangi kablolar çalışır.** PC bağlantısı iki bilgisayar arasında bir ağ ister; kablo bir ağ oluşturmalıdır:
- bir Ethernet kablosu (doğrudan ya da bir anahtar / yönlendirici üzerinden) ya da her tarafta bir USB / USB-C Ethernet bağdaştırıcısı;
- bir USB "link" / "bridge" kablosu (iki bilgisayarda da bir ağ bağdaştırıcısı olarak görünür);
- bir USB-C kablosu yalnızca iki USB4 / Thunderbolt portu arasında, sistemin üzerinden ağ sunduğu yerde (Windows:
  USB4 / Thunderbolt ağı; Linux: thunderbolt-net modülü). Sıradan bir USB-C portu (USB 3) iki bilgisayarı bağlayamaz:
  iki tarafta da hiçbir şey görünmez.
Kablo bir ağ oluşturduğunda iki bilgisayar da yeni bir ağ bağdaştırıcısı gösterir (Windows: Ayarlar > Ağ ve internet;
Linux: `ip link`); olmadan PC bağlantısının arayacağı bir şey yoktur.

Bağlantı şifrelidir (TLS). Her kurulumun kendi sertifikası vardır; kod iki sertifikadan üretilir, bu yüzden araya
giren üçüncü bir bilgisayar farklı bir kod gösterir.

### rclone uzak depoları
rclone desteği programla birlikte gelen bir eklentidir (yanında `plugins/rclone`; Yapılandırma > Eklentiler onu
kapatabilir). [rclone](https://rclone.org/downloads) kuruluyken (PATH'te ya da `FM_RCLONE` ile), düğme çubuğunun Eklentiler
menüsündeki **rclone uzak depoları** rclone yapılandırmanızın uzak depolarını listeler; bunları `rclone config` ile kurun. Parolaları ve belirteçleri
rclone'da kalır. NextCommander rclone'u bir kez yerel API sunucusu olarak başlatır (yalnızca 127.0.0.1 üzerinde, rastgele
bir parolayla) ve kapanırken durdurur. Dosyalar geçici dosyalar üzerinden aktarılır ve hizmet yapabiliyorsa zamanlarını korur.

### SSH tünelleri
**Bağlantı > SSH tünelleri...** kayıtlı bir SSH, SFTP ya da SCP bağlantısı üzerinden geçen port yönlendirmelerini tutar
(girişi: agent, anahtar ya da kayıtlı parola, atlama sunucusu, proxy ve sunucu anahtarı dosyalarında olduğu gibi kullanılır).
Her tünel bir karttır; durumu (çalışıyor, başlıyor, durdu ya da nedeniyle başarısız), **Başlat** / **Durdur**,
**Düzenle**, **Sil** ve **NextCommander ile başlat** vardır. **Yeni tünel** şunları sorar:
- türü: **Yerel** (bu bilgisayarın bir portu, sunucunun gördüğü bir sunucu ve porta götürür, örneğin 127.0.0.1:15432
  sunucunun arkasındaki `db.internal:5432` veritabanına), **Uzak** (sunucunun bir portu, bu bilgisayarın gördüğü bir
  sunucu ve porta götürür, örneğin sunucunun 8080 portu buradaki `localhost:3000`'e) ya da **SOCKS** (bu bilgisayarda
  bir SOCKS proxy'si: ona ayarlanmış tarayıcılar ve araçlar sunucudan çıkar);
- üzerinden geçeceği bağlantı, nerede dinleyeceği (127.0.0.1: yalnızca bu bilgisayar; 0.0.0.0: ağ da; uzak tünel
  için sunucunun buna `GatewayPorts` ile izin vermesi gerekir) ve nereye gideceği.

**NextCommander ile başlat** işaretli tüneller program başlarken başlar; tüm tüneller program kapanırken durur. Başlama,
durma ve hatalar (zaten kullanımda olan bir port, kopan bir bağlantı) bağlantının günlüğüne yazılır.

### Uzak oturumlar: RDP, VNC, SSH, Telnet, rsh, seri port

Terminalde açılan bir oturumun (SSH, Telnet, rsh, seri port) terminal türüyle birlikte sekmede ayarlanan kendi görünümü
olabilir: **Yazı tipi** ve **Yazı boyutu** (boş: terminalinki), bir **Renk düzeni** (koyu, açık, Solarized,
*Üretim (koyu kırmızı)*, siyah üzerine yeşil; ya da terminalin renkleri) ve bir **Sekme rengi** (sekmesinde bir şerit ve
ton), örneğin bir üretim sunucusu test sunucusuyla karıştırılmasın diye kırmızı.
Bunlar başka bir bilgisayardaki oturumlardır, klasör değil: birini bağlantı yöneticisinde (ya da *Bağlantı > Kayıtlı
bağlantılar*, `nc_OpenConnection name`) seçmek paneli değiştirmez.

- **Sekmede uzak masaüstü** (Windows ve Linux): oturum ana pencerenin bir sekmesinde, dosyalar sekmesinin yanında
  (seçenekler gibi) açılır ve masaüstü sekmenin boyutunu izler (sabit bir *Penceredeki boyut* ona sığacak şekilde
  ölçeklenir). Windows onu kendi Uzak Masaüstü denetimiyle (Uzak Masaüstü Bağlantısı'nın motoru), Linux X11
  FreeRDP ile (`xfreerdp3` ya da `xfreerdp`) çizer. Birkaç oturum açık olabilir, her birinin bağlantının adını taşıyan
  bir sekmesi vardır; gizli olanlar çalışmayı sürdürür; sekmenin boyutu değiştiğinde uzak masaüstü onu izler
  (Windows 8.1 / Server 2012 R2 ve sonrası; Linux: sekmedeki FreeRDP penceresi yeniden boyutlandırılır). Orada yazmak
  için masaüstüne tıklayın (tuşlar o zaman uzak bilgisayara gider); üstteki sekmeler geri döndürür. Hiçbiri
  kayıtlı değilse parola sorulur. Oturum bitince sekme bunu söyler (neden bağlanamadığı, sistemin sözleriyle) ve
  *Yeniden bağlan* ya da *Kapat* sunar; sekmenin × düğmesi bağlantıyı keser. Oturumun üstündeki başlık çubuğunun
  seçenekleri vardır: **⛶ Tam ekran** (Windows: Uzak Masaüstü'nün bağlantı çubuğuyla tüm ekran; geri yükle düğmesi
  sekmeye döner, küçült düğmesi NextCommander'ı küçültür; sekmeyi izleyen bir masaüstü bu arada ekranın boyutunu alır),
  **⤢ Sekmeye sığdır** (Windows: uzak masaüstünü sekmeye sığacak şekilde ölçekler) ve **⟳ Yeniden bağlan**. Bağlantının
  ayarlarındaki *Tam ekran* ve *İstemcinin kendi penceresinde aç* bunun yerine, aşağıda anlatıldığı gibi istemcinin kendi
  penceresini açar. Windows'ta *Etkin panelin klasörünü paylaş* klasörün sürücüsünü paylaşır. macOS'ta onu bir sekmede
  göstermenin yolu yoktur: Windows App açılır.
- Kendi penceresinde **uzak masaüstü (RDP)** sistemin istemcisinde açılır: Windows'ta *Uzak Masaüstü Bağlantısı* (mstsc), macOS'ta Microsoft'un
  **Windows App**'i (App Store'dan), Linux'ta **FreeRDP** (`xfreerdp3`, `sdl-freerdp3`, `xfreerdp`) ya da **Remmina**;
  hiçbiri yoksa neyin kurulacağı söylenir. Ayarlar: sunucu, port (3389), kullanıcı (`DOMAIN\user`), *Tam ekran*
  (aksi halde masaüstü boyutunu izleyen bir pencere) ve *Parolayı kaydet*. Kayıtlı bir parola istemciye komut satırı
  olmadan verilir: Windows'ta yalnızca geçerli oturum için kimlik bilgisi deposuna gider (mstsc'nin baktığı yer olan
  `TERMSRV/server` olarak), Linux'ta FreeRDP'nin standart girdisine. FreeRDP burada parolayı kendisi soramaz, bu yüzden
  Linux'ta kayıtlı yoksa parola sorulur. macOS'ta Windows App kendisi sorar ve hatırlar.
  Uzak bilgisayar uzak masaüstü bağlantılarına izin vermelidir: Windows Pro / Server (Home değil) ya da xrdp'li Linux.
- **Telnet** dahili terminalde (Ctrl+`) bağlantının adını taşıyan bir sekme açar. Sunucu kullanıcıyı ve parolayı
  orada sorar. Terminal xterm olduğunu söyler ve boyutunu gönderir, değiştiğinde yeniden. Sunucu bağlantıyı
  kapattığında sekme son ekranla kalır; Enter yeniden bağlanır. Telnet sekmeleri bir sonraki açılışta kabuklarla
  birlikte yeniden açılmaz.
  Telnet **şifreli değildir**: yazılan her şey, parola dahil, ağda okunabilir. Cihazda varsa SSH'ı tercih edin.
- **Uzak masaüstü ekstraları:** *Etkin panelin klasörünü paylaş* etkin panelin klasörünü oturumda görünür kılar
  (FreeRDP o klasörü adıyla paylaşır; Uzak Masaüstü Bağlantısı yalnızca tüm sürücüleri paylaşır, bu yüzden klasörün
  sürücüsünü paylaşır). Bağlantı yöneticisindeki **Dosyalar (SMB)** aynı bilgisayarın paylaşımlarını panelde açar:
  o sunucuya kayıtlı bir SMB bağlantısıyla, yoksa uzak masaüstünün kullanıcısıyla.
- **SSH** sunucunun bir kabuğunu dahili terminalin bir sekmesinde açar. SFTP'nin yaptığı gibi giriş yapar: parola
  (bir pencerede sorulur ya da kayıtlıdır), özel bir anahtar dosyası ya da SSH agent; ayarlıysa bir atlama sunucusu
  ya da proxy üzerinden. Sunucunun anahtarı SFTP'deki gibi denetlenir (aynı sunucuda SFTP için güvenilen bir anahtara
  burada da güvenilir; sabitlenmiş bir anahtar eşleşmelidir). Terminal türü (başka bir şey ayarlamadıysanız
  xterm-256color) ve pencere boyutu sunucuya gider, pencere değiştiğinde yeniden. *Açılırken komut* önce kabuğa yazılır
  (örneğin `tmux attach`). `exit` oturumu kapatır; sekme son ekranla kalır ve Enter yeniden bağlanır.
  **Dosyalar (SFTP)** (bir SSH sekmesi gösterilirken terminalin başlığında ve bağlantı yöneticisinde) sunucunun
  dosyalarını etkin panelin yeni bir sekmesinde açar: aynı sunucuya kayıtlı bir SFTP ya da SCP bağlantısıyla, yoksa
  oturumun kendi girişiyle (anahtar dosyası, agent, atlama sunucusu, proxy ve kayıtlı parolası). Kabuk klasörünü
  bildirdiğinde (birçok komut istemi gibi OSC 7), dosyalar o klasörde açılır; aksi halde kökte.
- **VNC** bu bilgisayarın bir VNC görüntüleyicisinde açılır: bir `vncviewer` (TigerVNC, UltraVNC, TurboVNC), RealVNC Viewer
  ya da TightVNC (Windows'ta kurulu oldukları yerde de), macOS'un Ekran Paylaşımı ya da Linux'ta Remmina / KRDC.
  Port 5900 `:0` ekranıdır, 5901 `:1` ekranı ve böyle devam eder. Seçenekler: tam ekran, yalnızca görüntüleme (tuş ya da
  fare gönderilmez). Görüntüleyici parolayı sorar.
- **rsh / rlogin** SSH'ı olmayan eski Unix sunucuları içindir. Komutsuz terminalde bir rlogin oturumudur (port 513);
  komutla rsh onu çalıştırır (port 514) ve sekme çıktısını gösterir. Parola yoktur: sunucu bu bilgisayarı `.rhosts` ya da
  `hosts.equiv` ile içeri alır (*bu bilgisayardaki kullanıcı* ve *sunucudaki kullanıcı* ile); rlogin, almadığında
  terminalde bir parola ister. rlogin sunucu sorduğunda pencerenin boyutunu söyler, pencere değiştiğinde yeniden
  (doğrudan bağlantıda). Bağlantı, sunucuların genellikle istediği gibi, bu bilgisayar izin veriyorsa 1024'ün altındaki
  bir porttan gelir (Linux ve macOS'ta yalnızca root olarak). Hiçbir şey şifrelenmez.
- **Seri port** bu bilgisayarın bir portunu (Windows'ta `COM3`; Linux'ta `/dev/ttyUSB0`, `/dev/ttyACM0`;
  macOS'ta `/dev/cu.usbserial-...`; yalın bir ad `/dev/<ad>` demektir) dahili terminalin bir sekmesinde açar: bir
  anahtarın, yönlendiricinin, sunucunun ya da kartın konsolu. Port kutusu bulunan portları sunar. Ayarlar: Genel
  sekmesinde hız (300 ile 921600 arası; genellikle 9600 ya da 115200); Gelişmiş sekmesinde veri bitleri, eşlik, dur
  bitleri, akış denetimi (yok, XON / XOFF, RTS / CTS), DTR, terminal türü, karakter kümesi, Geri tuşunun ve Enter'ın
  ne gönderdiği (varsayılan CR) ve yerel yankı (kapalı: cihazlar kendileri yankılar). `serial://COM3:115200` bir
  portu o hızla açar. *Dene* portun açıldığını denetler. Linux'ta kullanıcı `dialout` grubunda olmalıdır (bazı
  sistemlerde `uucp`).

Bağlantı yöneticisindeki *Dene*, sunucunun portta yanıt verdiğini denetler.

### Gelişmiş sekmesinin seçenekleri

Zaman aşımı ve eşzamanlı bağlantı sayısının yanı sıra, bir bağlantının **Gelişmiş** sekmesinde türünün seçenekleri vardır:

| Tür | Seçenekler |
| --- | --- |
| SFTP, SCP | Atlama sunucusu; canlı tutma; istek boyutu (SFTP: daha büyük istekler uzak sunucularda daha hızlıdır); sabitlenmiş sunucu anahtarı. |
| FTP, FTPS | Dosya adı kodlaması; klasör listeleme (sunucuda varsa MLSD, ya da her zaman LIST); sunucunun saat dilimi (yerel saatleri listeleyen sunucular için); kendi etkin mod portları; canlı tutma (NOOP); FTPS: veri bağlantılarını da şifrele, TLS sürümü, sabitlenmiş sertifika. |
| WebDAV | Kimlik doğrulama (sunucunun istediği gibi, Basic, Digest, NTLM, Negotiate); HTTPS: TLS sürümü, sabitlenmiş sertifika. |
| Amazon S3 | Sunucu tarafı şifreleme ve KMS anahtarı; yüklemelerin depolama sınıfı (STANDARD_IA, GLACIER_IR, DEEP_ARCHIVE ...); yüklemelerin ACL'si; büyük yüklemelerin parça boyutu; isteyen öder; sanal barındırmalı adresler; düz HTTP; TLS sürümü, sabitlenmiş sertifika. |
| Azure | Yüklenen blob'ların erişim katmanı (Hot, Cool, Cold, Archive); düz HTTP; TLS sürümü, sabitlenmiş sertifika. |
| SMB | Gizli yönetimsel paylaşımları göster (C$, ADMIN$). |
| Google Drive, OneDrive, Box | Hizmetin çöp kutusuna değil kalıcı olarak sil (OneDrive: iş / okul hesapları; kişisel olanlar geri dönüşüm kutusunu korur). Google Drive: Google belgeleri Office, OpenDocument ya da PDF dosyaları olarak. |
| Uzak masaüstü | Kendi sekmeleri. **Görünüm:** bir sekmede ya da istemcinin kendi penceresinde, tam ekran, tüm monitörler, penceredeki boyut, renkler. **Yerel kaynaklar:** ses (burada, uzak bilgisayarda, yok) ve kalitesi, mikrofon, paylaşılan pano, yazıcılar, sürücüler, etkin panelin klasörü, akıllı kartlar. **Deneyim:** bağlantının hızı (algılanan, ya da LAN'dan modeme) ve izin verdiği görsel efektler (arka plan, yazı yumuşatma, masaüstü birleştirme, sürüklerken pencere içeriği, animasyonlar, görsel stiller; her biri seçilen hızla değiştirilebilir). **Gelişmiş:** Uzak Masaüstü Ağ Geçidi, Windows tuş birleşimlerinin gittiği yer, kendiliğinden yeniden bağlanma, yönetimsel oturum. Hepsi Uzak Masaüstü Bağlantısı'nın .rdp dosyasına, bir sekmenin denetimine ve FreeRDP'nin argümanlarına gider. |
| Telnet | Terminal türü, karakter kümesi, Geri tuşunun (DEL ya da Ctrl+H) ve Enter'ın (CR LF, CR NUL, LF) ne gönderdiği, yerel yankı, canlı tutma. |
| SSH | Atlama sunucusu, canlı tutma, sabitlenmiş sunucu anahtarı, açılırken komut, terminal türü, karakter kümesi. |
| VNC | Tam ekran, yalnızca görüntüleme. |
| rsh / rlogin | Komut (boş: rlogin), terminal türü, karakter kümesi; kullanıcılar Giriş sekmesinde. |
| Seri port | Veri bitleri, eşlik, dur bitleri, akış denetimi, DTR, terminal türü, karakter kümesi, Geri tuşu, Enter, yerel yankı; hız Genel sekmesinde. |

**Sabitlenmiş** bir sunucu anahtarı ya da sertifika (`SHA256:...`) bağlantı için güvenilen tek anahtardır: başkası
sorulmadan reddedilir.

## Güvenlik

Bir SFTP sunucusuna ya da şifreli (TLS) bağlantı kullanan bir sunucuya ilk kez bağlandığınızda NextCommander
anahtarının ya da sertifikasının parmak izini gösterir ve ona güvenmenizi ister. Bunu sunucunun
yöneticisinin verdiğiyle karşılaştırın. Anahtar sonradan değişirse net bir uyarı alırsınız, çünkü bu birinin
bağlantıyı araya girerek dinlediği anlamına gelebilir.

## Kayıtlı parolalar ve ana parola

- İlk kez giriş yaptığınızda parolayı kaydetmeyi seçebilirsiniz. Yalnızca başarılı bir girişten sonra kaydedilir ve
  yanlış çıkarsa yeniden sorulur.
- Kayıtlı parolalar [ayarlar klasöründe](settings-folder.md) şifreli olarak saklanır.
- **Paylaşılan girişler:** birkaç bağlantı tek bir girişi kullanabilir (bir etki alanı hesabı, web sunucularının dağıtım
  kullanıcısı). Bağlantı düzenleyicisinde **Giriş**, bağlantının kendi kullanıcı ve parolası ile paylaşılan bir giriş
  arasında seçim yapar; yanındaki **Girişler...** onları düzenler: ad, kullanıcı, parola (şifreli depoda bir kez
  tutulur) ve SFTP, SCP ve SSH için bir özel anahtar dosyası. Bağlantılar girişin kullanıcısını ve anahtarını alır;
  parolası bir kez sorulur ve sonra hepsi tarafından kullanılır. Girişi değiştirmek hepsini değiştirir; silmek
  onları kullanıcıyla ve kendi parolalarıyla bırakır. Dışa aktarılmış bir bağlantı dosyası kullanıcıyı tutar, girişi
  değil. Yeni bağlantı sayfası da (varsa) paylaşılan girişleri **Giriş** altında sunar.
- Bir **ana parolayla** (*Bağlantı > Ana parola...*) parolalar ondan üretilen bir anahtarla şifrelenir.
  Oturum başına bir kez sorulur. Ana parolayı kaldırmak için yeni ana parolayı boş bırakın.
- Ana parola olmadan parolalar, bu bilgisayarda kullanıcı hesabınıza bağlı bir anahtarla korunur.

## Ağ komşuluğu

*Bağlantı > Ağ (yerel ağdaki makineler)* ya da sürücü kutusundaki `\`, yerel ağınızdaki makineleri klasörler olarak
gösterir.

- NextCommander bilinen komşuları, kendilerini duyuran cihazları (NAS, Mac, Linux) ve SFTP, FTP, SMB ve web
  portlarında yanıt veren makineleri arar. Ctrl+R yeniden tarar.
- Bir makinenin içinde hizmetlerini (SFTP, FTP, WebDAV) ve Windows'ta paylaşılan klasörlerini görürsünüz. Linux ve
  macOS'ta bir "SMB" girdisi paylaşımları listeler.
- Bir hizmeti açmak ona bağlanır: o makine için kayıtlı bağlantınız varsa onunla, yoksa sizden kullanıcı adı ve
  parola istenir.
- *Bağlantı > Yönetici paylaşımlarını göster* `C$` gibi gizli yönetimsel paylaşımları da listeler (Windows).
- Bu görünüm yalnızca gezinmek içindir; içine kopyalayamaz ya da içinden silemezsiniz.

## Proxy

WebDAV, S3, Azure Blob, Google Drive, OneDrive, Dropbox (oturum açmaları da), URL indirmeleri ve güncelleme denetimi,
[Yapılandırma penceresinin](configuration.md) *İşlem > Proxy* sayfasında seçilen proxy üzerinden gider:

- **Sistemin proxy'sini kullan** (varsayılan): Windows'un İnternet ayarlarındaki proxy, macOS ağ ayarları ya da
  Linux'ta `http_proxy` / `https_proxy` / `no_proxy` değişkenleri.
- **Proxy yok**: sistemde bir proxy olsa bile her zaman doğrudan.
- **Bu proxy'yi kullan**: sunucusu ve portu olan (ya da `http://proxy:3128`, `socks4a://proxy:1080` ya da
  `ssh://bastion:22` gibi tam bir adres) bir proxy, proxy istediğinde bir kullanıcı adı ve parola (parola diğer kayıtlı
  parolalarınızla birlikte şifreli tutulur) ve doğrudan giden adresler (`localhost; *.local;
  10.*`; isterseniz noktasız yerel adlar da). Türü:
  - **HTTP**: bir HTTP proxy'si (diğer bağlantılar CONNECT ile).
  - **HTTPS (proxy'ye TLS)**: aynısı, şifreli bir bağlantı üzerinden erişilir; sertifikası geçerli olmalıdır.
  - **SOCKS5**, **SOCKS4a**, **SOCKS4**: SOCKS proxy'leri (SOCKS5 kullanıcı adı ve parolayla; SOCKS4 kullanıcı adını
    kullanıcı kimliği olarak gönderir; SOCKS4 burada çözümlenen bir IPv4 adresi ister, SOCKS4a adı gönderir).
  - **SSH tüneli**: `ssh -D` gibi bir SSH sunucusu: her bağlantı tek bir SSH oturumunun bir kanalıdır. Parola ya da
    özel bir anahtar dosyasıyla giriş yapar (parolası parola olarak); sunucunun anahtarı her SSH sunucusununki gibi
    denetlenir ve sorulur.

  **FTP, SFTP ve SMB bağlantıları için de** (varsayılan olarak açık) bunlar da onun üzerinden gider: FTP (veri
  bağlantıları da), SFTP ve SCP, SMB ve Telnet. Bir HTTP proxy'si onların portlarına (21, 22, 445 ...) CONNECT'e
  izin vermelidir; SOCKS ve SSH proxy'leri her bağlantıyı alır.
- **Otomatik yapılandırma betiği (PAC)**: bir proxy otomatik yapılandırma betiğinin adresi (`http://wpad.example.com/wpad.dat`
  ya da bir dosya). `FindProxyForURL` işlevi her adresin yolunu seçer: doğrudan ya da adını verdiği HTTP, HTTPS ya da
  SOCKS proxy'si üzerinden (yanıtının ilki). Yukarıdaki kullanıcı adı ve parola bu proxy'ler için kullanılır.
  Betik proxy'siz okunur ve yarım saat tutulur; okunamayan ya da başarısız olan bir betik bağlantıları doğrudan gönderir.

**Dene**, pencerede olduğu gibi ayarlarla bir adres ister ve proxy üzerinden gidip gitmediğini, yanıtı ve ne kadar
sürdüğünü söyler. Zaten açık olan bağlantılar yeniden bağlanana kadar yollarını korur.
Sistemin proxy'si yalnızca web istekleri için kullanılır.

### Bir bağlantının kendi proxy'si

Bağlantı yöneticisinde bir bağlantının **Proxy** sekmesi, programın ayarı ne olursa olsun yolunu seçer:

- **Programın ayarı**: yukarıdaki gibi (varsayılan).
- **Proxy yok**: her zaman doğrudan.
- **Kendi proxy'si**: yukarıdaki türlerden biri, sunucusu, portu, kullanıcısı ve parolasıyla (bir SSH tüneli ayrıca
  özel bir anahtarla). Parola, bağlantı kaydedildiğinde parola deposunda tutulur.

Uzak masaüstü (RDP) ve VNC bağlantılarının, seri portların ve PC bağlantısının Proxy sekmesi yoktur: RDP ve VNC
görüntüleyicileri kendileri bağlanır ve bir PC bağlantısı yerel ağda kalır.

## Web'den indirme

- Pano web adresleri içeriyorsa bir panelde Ctrl+V bunları indirmeyi önerir.
- *Bağlantı > URL'den indir...* tek bir adresi indirir, *Listedeki adresleri indir...* bir metin dosyasındaki tüm
  adresleri.
- Yarım kalan bir indirme kaldığı yerden devam eder. İndirilen dosyalar sistemin "internetten indirildi" işaretini alır.
