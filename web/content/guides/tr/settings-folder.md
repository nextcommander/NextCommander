# Ayarlar klasörü

[README'ye dön](../../README.md)

NextCommander ayarlarınızı, kayıtlı parolalarınızı ve kendi temalarınızı, simge paketlerinizi ve dillerinizi tek bir
klasörde tutar.

## Nerede

| Sistem | Klasör |
|---|---|
| Windows | `%APPDATA%\NextCommander` (genellikle `C:\Users\<you>\AppData\Roaming\NextCommander`) |
| Linux | `~/.config/NextCommander` (ya da ayarlıysa `$XDG_CONFIG_HOME/NextCommander`) |
| macOS | `~/Library/Application Support/NextCommander` |

Açmanın en hızlı yolu: [Yapılandırma penceresinin](configuration.md) *Çeşitli* sayfasındaki **Ayar klasörünü aç**
düğmesi.

## Taşınabilir kip

Programın yanında `portable.json` adlı bir dosya varsa NextCommander her şeyi bunun yerine programın kendi
klasöründe tutar. Windows için taşınabilir zip bu dosyayı zaten içerir; böylece NextCommander'ı bir USB bellekten
çalıştırabilir ve ayarlarınızı yanınızda taşıyabilirsiniz.

## İçinde neler var

| Öğe | İçerik |
|---|---|
| `settings.json` | Yapılandırma penceresinin yazdığı tüm ayarlarınız. Onu elle düzenlemeniz hiç gerekmez. |
| `secrets.json` | Kayıtlı parolalar, şifrelenmiş (bkz. [Bağlantılar](connections.md#saved-passwords-and-the-master-password)). |
| `themes/` | Kendi [renk temalarınız](themes.md). |
| `iconpacks/` | Kendi [simge paketleriniz](icon-packs.md). |
| `languages/` | Kendi [dil dosyalarınız](languages.md). |
| `plugins/` | Eklentiler, her biri ayrı bir alt klasörde. Programın yanındaki bir `plugins` klasörü de çalışır. |
| `index/` | [Dosya dizini](tools.md#file-index). |
| `thumbs/` | Diskte tutulan küçük resimler (bu seçenek açıkken). |
| `logs/` | Program günlüğü (`nextcommander-<date>.log`, *Günlük dosyası* altından kapatılabilir), açıkken ve başka dosya verilmemişse işlem günlüğü `operations.log` ve NextCommander hiç başlayamazsa bir çökme raporu. |

`themes`, `iconpacks` ve `languages` klasörleri ilk kez gerektiğinde oluşturulur; bunları kendiniz de
oluşturabilirsiniz. Bunlardan birindeki bir dosya, aynı ada sahip yerleşik bir dosyanın yerini alır.

## Otomatik yedekler

NextCommander her başladığında ve ayarları yüklendiğinde, orada en yeni kopyayla aynı değilse `settings.json`, ayarlar
klasörünün içindeki `backups/` klasörüne kopyalanır (`settings-<date>-<time>.json`). Bir geri yüklemeden ve bir içe
aktarmadan hemen önce de kopya alınır. Bu otomatik yedeklerden son **7** tanesi saklanır; kendi yedekleriniz ve
sabitlenmiş olanlar siz silene kadar kalır (her yedek hakkında not edilenler `backups/index.json` içindedir).
`settings.json` bozulmuşsa `settings.json.corrupt` olarak ayrı tutulur ve yüklenen en yeni yedek onun yerini alır;
başlangıçta bir ileti hangisi olduğunu söyler.

*Yapılandırma > Ayarları bir yedekten geri yükle...* (ayrıca Yapılandırma penceresinin *Çeşitli* sayfasında bir düğme)
solda yedekleri gün gün, en yeni en üstte olacak şekilde gösteren bir pencere açar: her birinin ne zaman ve neden
alındığı (başlangıçta, bir geri yüklemeden ya da içe aktarmadan önce, sizin tarafınızdan, bir dosyadan eklenmiş), adı,
sabitlenmişse bir kilit, kullanımdaki ayarlar için *Şimdikiyle aynı* ve altındakiyle aynı ayarları tutan biri için
*Çoğalt*. Bir arama (tarih, ad, bağlantı veya Hotlist adları) ve *Tümü*, *Benimkiler*, *Otomatik*, *Sabitlenenler*
süzgeçleri listeyi daraltır.

Sağda, seçili yedek için, onu geri yüklemenin neyi değiştireceği parça parça yer alır: **Ayarlar** (her seçenek;
pencere ve panellerin klasörleri kalır), **Bağlantılar**, **Hotlist**, **Kısayollar**, **Düğme çubukları** ve **Renk
teması**. Her parça kaç değişikliği olduğunu ve açıldığında her değişikliği gösterir (geri eklenen, kaldırılan,
şundan → şuna değişen). Yalnızca işaretli parçalar geri yüklenir; gerisi olduğu gibi kalır. *Geri yükle...* önce sorar,
kullanımdaki ayarları bir yedek olarak saklar ve NextCommander'ı yeniden başlatır; geri yüklemeyi geri almanın yolu bu
yedektir ("Geri yüklemeden önce").

![Ayarları bir yedekten geri yükle](../screenshots/restore-backups.png)

- **Şimdi yedekle** ayarları olduğu gibi, yazdığınız bir adla saklar; **Dosya aç...** yedeklere bir ayar dosyası
  (örneğin başka bir bilgisayardan) ekler.
- F2 veya kalem seçili yedeği adlandırır, kilit onu sabitler (hiçbir zaman otomatik kaldırılmaz), Del veya çöp kutusu
  onu siler (listenin altındaki *Geri al* onu geri getirir; sabitlenmiş olan önce sabitlemeden çıkarılmalıdır).
  *Klasörü aç* `backups/` klasörünü gösterir.

## Yedekleme ve başka bir bilgisayara taşıma

En kolay yol: *Yapılandırma > Ayarları dışa aktar...* `settings.json` (tüm seçenekler, bağlantılar, Hotlist,
kısayollar, düğme çubukları ...) ile kendi temalarınızı, simge paketlerinizi ve dil dosyalarınızı içeren tek bir zip
dosyası yazar. Yeni kurulumda *Yapılandırma > Ayarları içe aktar...* onu geri okur: dosyanın neleri içerdiğini gösterir,
geçerli ayarları ayarlar klasörünün içindeki `backups/` klasöründe saklar ve NextCommander'ı içe aktarılan ayarlarla
yeniden başlatır. İkisi de Yapılandırma penceresinin *Çeşitli* sayfasında birer düğmedir.

- Kayıtlı parolalar yalnızca bir ana parola onları koruyorsa birlikte gider (dışa aktarma sorar); o zaman ana parola
  yeni bilgisayarda da gerekir. Ana parola olmadan yalnızca kaydedildikleri bilgisayarda açılırlar.
- Dosya dizini, küçük resimler, günlükler ve eklentiler dışa aktarmanın parçası değildir.

Bunu elle de yapabilirsiniz: NextCommander'ı kapatın ve tüm klasörü kopyalayın. Ayarlarınızı başka bir bilgisayara
taşımak için klasörü orada aynı yere kopyalayın. Windows'ta ana parolayla korunmayan kayıtlı parolalar ilk bilgisayardaki
kullanıcı hesabınıza bağlıdır ve yenisinde yeniden girilmeleri gerekir.

## Eski ad

NextCommander eskiden FileManager olarak adlandırılıyordu. İlk başlangıçta henüz bir `NextCommander` ayarlar klasörü
yoksa eski `FileManager` klasöründeki ayarlar kopyalanır. Eski klasör olduğu gibi bırakılır.
