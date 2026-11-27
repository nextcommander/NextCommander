# Eklentiler

[README'ye dön](../../README.md)

Eklenti, NextCommander'ın başlangıçta yüklediği bir .NET derlemesidir (assembly). Şunları ekleyebilir:

| Ne | Arayüz | Nerede görünür |
|---|---|---|
| Bir dosya sistemi | `IFileSystemProvider` | Eklentinin şemasıyla başlayan yollar (`hello://host/folder`) bir panelde açılır; kopyalama, taşıma, silme, görüntüleme ve düzenleme bu dosya sistemi ile diğer tüm dosya sistemleri arasında çalışır. |
| Başlangıç noktası | `IPluginEntryPoint` (dosya sisteminde) | Düğme çubuğundaki **Eklentiler** menüsünde bir giriş. |
| Geçerli klasör için bir yer | `IPluginFolderEntry` (dosya sisteminde) | Etkin panelin klasörü uygun olduğu sürece **Eklentiler** menüsünün en üstünde bir giriş (örneğin bir depoda *Git geçmişi*). |
| Geçerli klasör için eylemler | `IPluginFolderActions` (dosya sisteminde) | Etkin panelin klasörü ve seçili öğeleri için **Eklentiler** menüsünde ve dosya listesinin kendi menüsünün en üstünde girişler (örneğin Git'in *Hazırla* ve *Commit...* eylemleri); `IPluginDialogs` aracılığıyla soru sorabilirler. |
| Yukarı çıkma yolu | `IOriginProvider` (dosya sisteminde) | Dosya sisteminin kökünden ".." ile gelinen klasöre geri dönülür. |
| Okunacak bir arşiv biçimi | `IContainerProvider` (dosya sisteminde) | Böyle bir dosyada Enter / Ctrl+PgDn onu klasör gibi açar. |
| Sütunlar | `IColumnProvider` | *Göster > Görünüm > Özel görünüm modları > Görünüm modlarını düzenle...*: eklentinin alanları bir görünüm moduna eklenebilir. |
| Yazılacak bir arşiv biçimi | `IPackerPlugin` | Paketle penceresinde (Alt+F5) bir paketleyici. |
| Bir görüntüleyici | `IViewerPlugin` | Lister (F3), istediği dosyaları gösterir (6 tuşu). |
| Girişleri için eylemler | `IPluginEntryActions` (dosya sisteminde) | Dosya listesinin sağ tık menüsünün en üstünde (bir konteyner başlat ...). |
| Ayarlar | `IConfigurablePlugin` | Yapılandırmanın Eklentiler sayfasında bir grup; eklentiler penceresinde **Yapılandır...**. |

NextCommander'ın rclone, Git, SQLite, Docker, Kubernetes, sanal disk, paket, görüntüleyici ve GitHub / GitLab desteği
de birer eklentidir ([src/Plugins/NextCommander.Plugin.Rclone](../../src/Plugins/NextCommander.Plugin.Rclone),
[src/Plugins/NextCommander.Plugin.Git](../../src/Plugins/NextCommander.Plugin.Git),
[src/Plugins/NextCommander.Plugin.Sqlite](../../src/Plugins/NextCommander.Plugin.Sqlite),
[src/Plugins/NextCommander.Plugin.Docker](../../src/Plugins/NextCommander.Plugin.Docker),
[src/Plugins/NextCommander.Plugin.Kubernetes](../../src/Plugins/NextCommander.Plugin.Kubernetes),
[src/Plugins/NextCommander.Plugin.DiskImage](../../src/Plugins/NextCommander.Plugin.DiskImage),
[src/Plugins/NextCommander.Plugin.Packages](../../src/Plugins/NextCommander.Plugin.Packages),
[src/Plugins/NextCommander.Plugin.Viewers](../../src/Plugins/NextCommander.Plugin.Viewers),
[src/Plugins/NextCommander.Plugin.Repositories](../../src/Plugins/NextCommander.Plugin.Repositories); aşağıdaki
[Git](#git), [SQLite](#sqlite), [Docker](#docker), [Kubernetes](#kubernetes), [Sanal diskler](#virtual-disks),
[Paketler ve yükleyiciler](#packages-and-installers), [Görüntüleyiciler](#viewers) ve
[GitHub ve GitLab depoları](#github-and-gitlab-repositories) bölümlerine bakın) ve
[tests/SamplePlugin](../../tests/SamplePlugin), bir sütun ve `hello://` dosya sistemi içeren küçük bir örnektir.

- [Git](#git)
- [SQLite](#sqlite)
- [Docker](#docker)
- [Kubernetes](#kubernetes)
- [Sanal diskler](#virtual-disks)
- [Paketler ve yükleyiciler](#packages-and-installers)
- [Görüntüleyiciler](#viewers)
- [GitHub ve GitLab depoları](#github-and-gitlab-repositories)
- [Eklentileri kullanma](#using-plugins)
- [Eklenti yazma](#writing-a-plugin)
- [Dosya sistemleri](#file-systems)
- [Eklentiler menüsündeki yerler](#places-in-the-plugins-menu)
- [Sütunlar](#columns)
- [Paketleyiciler ve görüntüleyiciler](#packers-and-viewers)
- [Çeviriler](#translations)
- [Test ve sorun bulma](#testing-and-finding-problems)
- [Eklentiyi NextCommander ile birlikte sunma](#shipping-a-plugin-with-nextcommander)

## Git

Git eklentisi NextCommander ile birlikte gelir ve kurulu `git` programını kullanır (git-scm.com; ya da yolunu gösteren `FM_GIT`).

- **Git geçmişi:** etkin panel bir deponun klasörlerinden birindeyken **Eklentiler** menüsünün en üstünde *Git geçmişi*
  bulunur. Deponun geçmişini salt okunur klasörler olarak açar: **branches** (dallar), **tags** (etiketler) ve **commits**
  (geçerli dalın en yeni 300 commit'i; adları `2026-11-21 23.40 1a2b3c4 subject` biçimindedir). Her biri dosyaları o andaki
  hâliyle içerir: eski bir sürümü F3 ile görüntüleyin, bugünkü dosyayla karşılaştırın ya da dışarı kopyalayın. Dal adındaki
  eğik çizgi `⁄` olarak gösterilir (`feature⁄login`); böylece dal tek bir klasör olarak kalır. Geçmişin kökünden ".." depoya geri götürür.
- **Sütunlar:** *Git durumu* (Değişti, Değişti (hazırlandı), Eklendi, Silindi, Yeniden adlandırıldı, İzlenmiyor, Yok sayılıyor, Çakışma;
  bir klasör için: *İçinde değişiklik var* ya da *Yok sayılıyor*), *Son commit* (dosyayı veya klasörü değiştiren son
  commit'in konusu), *Commit yazarı*, *Commit tarihi* ve *Commit*. Bunları bir görünüm moduna ekleyin (*Göster > Görünüm > Özel görünüm modları >
  Görünüm modlarını düzenle...*). Arka planda doldurulurlar: her depo için birkaç saniyede bir `git status`, her klasör için bir `git log`.
- **Yazma:** bir deponun çalışma klasöründe **Eklentiler** menüsü (ve dosya listesinin kendi menüsü: Windows'ta Shift+sağ tık)
  seçili dosyalar için *Git: Hazırla (stage)*, *Git: Hazırlıktan çıkar*, *Git: Değişiklikleri at...* ve *Git: Değişiklikleri göster*,
  tek bir dosya için *Git: Dosya geçmişi...* (bir commit seçilir, değişiklikleri Lister'da gösterilir) ve depo için *Git: Commit...*
  (hiçbir şey hazırlanmamışsa Git'in bildiği dosyaların tüm değişikliklerini commit etmeyi önerir), *Git: Dal değiştir...* ve
  *Git: Yeni dal...* seçeneklerini sunar. Git reddettiğinde kendi iletisi gösterilir.
- **Sunucular ve birleştirme:** *Git: Getir (fetch)* (ve dalın ne kadar ileride / geride olduğu), *Git: Çek (pull)*, *Git: Gönder (push)* (sunucuda henüz
  olmayan bir dal gönderilir ve siz onaylarsanız izlenir) ve *Git: Dal birleştir...*. Çakışmalı bir çekme veya birleştirme
  dosyaları listeler; birleştirme beklerken menü *Git: Çakışmalar...* (dosya birleştirme penceresinde açılır: her
  çakışma için bir taraf seçin; hiç çakışma kalmadan kaydedilen dosya hemen hazırlanır), *Git: Birleştirmeyi bitir* (dosyalar çözülüp
  hazırlandığında) ve *Git: Birleştirmeyi iptal et* seçeneklerini sunar. Kullanıcı adları ve parolalar git'in kendi
  kimlik bilgisi yardımcısından gelir (Windows'ta Git Credential Manager, macOS'ta anahtar zinciri); SSH sunucuları SSH
  aracısının anahtarlarını kullanır. Terminalde hiçbir şey sorulmaz: git'in saklamadığı bir parola isteyen sunucu bunu söyler.

## SQLite

SQLite eklentisi NextCommander ile birlikte gelir ve sistemin SQLite kitaplığını kullanır: Windows'ta `winsqlite3.dll`,
Linux'ta `libsqlite3.so.0` (*libsqlite3-0* / *sqlite-libs* paketi, neredeyse her zaman vardır), macOS'ta `libsqlite3.dylib`;
`FM_SQLITE` başka bir kitaplığı gösterir. Onunla birlikte hiçbir şey kurulmaz.

- **Klasör olarak veritabanı:** Enter bir `.sqlite`, `.sqlite3`, `.db3` veya `.sdb` dosyasını ve SQLite veritabanı olan bir `.db`
  dosyasını açar (Windows'un `Thumbs.db` dosyası değildir); Ctrl+PgDn, veritabanı olan her dosyayı açar. Veritabanı her tablo ve görünüm
  için bir klasör ile `schema.sql` gösterir (tüm veritabanını oluşturan deyimler). Bir tablonun klasöründe
  `<table>.csv` (UTF-8, bir başlık satırı; Excel açar), `<table>.json` (nesne dizisi; BLOB'lar base64) ve indeksleri ile tetikleyicilerini içeren
  kendi `schema.sql` dosyası bulunur. Bir CSV veya JSON dosyasını dışarı kopyalamak tabloyu dışa aktarır; F3 onu görüntüler. Tablo adındaki
  eğik çizgi `⁄` olarak gösterilir. Kökten ".." veritabanı dosyasına geri götürür.
- **Salt okunur:** veritabanı hiçbir zaman değiştirilmez.
- **Veritabanı dosyasında Lister (F3):** bir özet: tablo, görünüm ve indeks sayısı, sonra her tablo; satır sayısı, sütunları (tür, birincil anahtar, NOT NULL, varsayılan) ve ilk 10 satırı ile, ve indeksler.

## Docker

Docker eklentisi NextCommander ile birlikte gelir ve kurulu `docker` komutunu kullanır (Docker Desktop veya Docker
Engine; `FM_DOCKER` podman gibi aynı komutlara sahip başka bir programı gösterir). Docker'ın kendisinin ayarlandığı şekilde çalışır
(geçerli bağlamı, `DOCKER_HOST`), bu yüzden uzak motorlar da çalışır.

- **Docker konteynerleri**, kayıtlı bağlantılarla birlikte listelenir (Bağlantı > Kayıtlı bağlantılar ve
  düğme çubuğundaki bağlantılar düğmesi). `docker:///` adresini açar: **(volumes)** klasörü ve çalışan ve durmuş konteynerler;
  adları `docker ps` çıktısındaki gibidir.
- **Bir konteynerin dosyaları:** çalışan bir konteynerde Enter, dosya sistemini `/` kökünden gösterir. Her panelde olduğu gibi görüntüleyin (F3), düzenleyin (F4), içeri
  ve dışarı kopyalayın, klasör oluşturun, yeniden adlandırın ve silin; kopyalanan dosyalar değiştirilme zamanını korur. Değişiklikler
  konteynerin kullanıcısı olarak (ya da root olarak, ayarlara bakın) çalışır ve üzerine yazılan dosya sahibini ve haklarını korur.
  Bir konteynerin kökünden ".." listeye geri götürür.
- **Durmuş bir konteyner** de açılır, ancak salt okunur olarak: dosyaları `docker cp` ile gelir (görüntüleme, dışarı kopyalama). Bir
  klasörü listelemek, altındaki her şeyi Docker'dan kopyalar; bu yüzden büyük bir klasör (büyük bir imajın kökü) biraz sürer. Bir
  dosyayı değiştirmek, konteynerin nasıl başlatılacağını söyler.
- **Başlat, durdur, yeniden başlat, duraklat, sürdür:** listedeki bir konteynerin sağ tık menüsü.
- **Volume'ler:** **(volumes)**, Docker volume'lerini listeler; bir volume klasör gibi açılır ve dosyaları değiştirilebilir.
  Ona, onu bağlayan küçük bir yardımcı konteyner aracılığıyla ulaşılır (`nc-volume-<volume>`, ayarlardaki imajdan,
  varsayılan olarak `busybox`, ilk seferde indirilir); yardımcılar NextCommander kapanınca kaldırılır.
- **Ayarlar** (*Yapılandırma > Eklentiler > Docker*): docker programı, konteynerlerde çalışan kullanıcı,
  volume açmak için kullanılan imaj.
- **Sınırlar:** kabuğu (`sh`) olmayan bir konteyner (örneğin "distroless" bir imaj) çalışırken listelenemez
  (durmuşken listelenebilir). Konteynerlerin ve volume'lerin kendisi docker ile oluşturulur ve kaldırılır.

## Kubernetes

Kubernetes eklentisi NextCommander ile birlikte gelir ve kurulu `kubectl` programını kullanır (`FM_KUBECTL` başka birini gösterir;
Docker Desktop ve Rancher Desktop bir tane getirir). kubectl'in kendi okuduğu kubeconfig ile (`KUBECONFIG`, yoksa ev klasöründeki
`.kube/config`) ya da ayarlarda gösterilen ile çalışır; böylece kubectl'in eriştiği her küme, kendi
oturum açma yöntemiyle (bulut araçları, sertifikalar, belirteçler) çalışır.

- **Kubernetes**, kayıtlı bağlantılarla birlikte listelenir (Bağlantı > Kayıtlı bağlantılar ve düğme çubuğundaki
  bağlantılar düğmesi). `k8s:///` adresini açar: kubeconfig'in bağlamları (context). Bir bağlam adındaki `/` (bir EKS bağlamı,
  `cluster/name` ile biten bir ARN'dir) `|` olarak gösterilir.
- **Her düzey için bir klasör:** bir bağlam ad alanlarını (namespace) listeler (bunları listeleme hakkı olmayan bir hesap bağlamının
  kendi ad alanını, yoksa `default` ad alanını görür); bir ad alanı pod'larını, her birinin oluşturulma zamanıyla listeler; bir pod
  konteynerlerini listeler, sonra bir konteyner dosya sistemini `/` kökünden gösterir. Konteyner düzeyi tek konteynerli bir pod için de
  her zaman vardır: bir yol hangi konteynerde olduğunu kendiliğinden söyler ve pod ikinci bir konteyner (sidecar) aldığında
  aynı kalır. ".." düzey düzey yukarı çıkar, bir bağlamdan bağlam listesine kadar.
- **Bir konteynerin dosyaları:** görüntüleyin (F3), düzenleyin (F4), içeri ve dışarı kopyalayın, klasör oluşturun, yeniden adlandırın ve silin, her panelde olduğu gibi;
  kopyalanan dosyalar değiştirilme zamanını korur. Her şey `kubectl exec` ile konteynerin kullanıcısı olarak çalışır ve
  üzerine yazılan dosya sahibini ve haklarını korur. Yalnızca çalışan bir konteyner açılabilir.
- **Sütunlar:** *Kubernetes durumu* (kubectl'in STATUS sütunu gibi: Running, CrashLoopBackOff, Terminating ...; kubeconfig'in geçerli
  bağlamı için *Geçerli bağlam*), *Hazır konteynerler*, *Yeniden başlatmalar* ve *Düğüm* bir görünüm moduna eklenebilir
  (*Göster > Görünüm > Özel görünüm modları > Görünüm modlarını düzenle...*).
- **Pod eylemleri** (bir ad alanında seçili pod'lar için dosya listesinin kendi menüsü ve Eklentiler menüsü): **Pod günlükleri**
  (son 500 satır, her konteynerin adı önünde olacak şekilde; Lister'da), **Pod'u açıkla**
  (`kubectl describe`), **Deployment'ı yeniden başlat** (`kubectl rollout restart`, yalnızca replica set aracılığıyla bir
  deployment'a ait pod için), **Pod'u sil...** (önce sorar; deployment onu yeniden oluşturur). Bir pod'un içindeyken
  günlükler (panelin içinde bulunduğu konteynerin) ve açıklama da vardır.
- **Ayarlar** (*Yapılandırma > Eklentiler > Kubernetes*): kubectl programı, kubeconfig dosyası, gösterilen günlük
  satırı sayısı.
- **Hatalar ne yapılacağını söyler:** kubectl yok, yanıt vermeyen bir küme, süresi dolmuş bir oturum, yetkisi olmayan bir hesap
  (RBAC), çalışmayan bir konteyner, kabuğu (`sh`) olmayan ve bu yüzden listelenemeyen bir imaj (örneğin "distroless"
  bir imaj). Ad alanlarının, pod'ların ve konteynerlerin kendisi kubectl ile oluşturulur ve kaldırılır.

## Sanal diskler

Disk imajı eklentisi NextCommander ile birlikte gelir ve sanal makine disklerini kendisi okur (DiscUtils ile), her sistemde ve
yönetici hakları gerekmeden.

- **Klasör olarak disk:** Enter, bu bilgisayardaki bir `.vhd` / `.vhdx` (Hyper-V, Virtual PC), `.vmdk` (VMware: tek dosya, bölünmüş
  veya anlık görüntülü), `.vdi` (VirtualBox) ya da `.dmg` (macOS, HFS+) dosyasını açar. Birden çok birimi olan bir disk, her biri için numarası, etiketi, dosya sistemi ve boyutuyla adlandırılmış
  bir klasör gösterir (`1 System (NTFS, 500 MB)`); tek birimli bir disk dosyalarını doğrudan gösterir. Her klasörde olduğu gibi görüntüleyin (F3), dışarı kopyalayın, arayın (Alt+F7), karşılaştırın ve klasör boyutlarını hesaplayın.
  Kökten ".." disk dosyasına geri götürür.
- **Dosya sistemleri:** NTFS, FAT / exFAT, ext2/3/4, XFS, Btrfs, HFS+ ve LVM mantıksal birimleri. BitLocker veya LUKS ile şifrelenmiş bir birim, APFS, ReFS ya da biçimlendirilmemiş bir birim
  listelenir ve açıldığında bunu söyler.
- **Fark disklerini ve anlık görüntüleri** (differencing), yanlarında bulunan üst disklerinden okur.
- **Salt okunur:** disk hiçbir zaman değiştirilmez; değiştirmek için dosyaları dışarı kopyalayın.
- **Disk dosyasında Lister (F3):** biçim, diskin ve dosyanın boyutu, bölüm tablosu (GPT / MBR), bir fark zincirinin
  disk dosyaları ve her birim; türü, dosya sistemi, etiketi, boyutu ve ne kadar dolu olduğuyla.
- **Bağlama** (Windows, `.vhd` / `.vhdx`): *Dosya > Arşiv > Disk kalıbını bağla*, diski sürücü harfleriyle
  Windows'un bağlamasını sağlar; bkz. [Arşivler](archives.md).

## Paketler ve yükleyiciler

Paketler eklentisi NextCommander ile birlikte gelir ve yazılım paketlerini ile yükleyicileri dpkg, rpm, msiexec veya docker olmadan, her sistemde
kendisi okur. Her şey salt okunurdur: değiştirmek için dosyaları dışarı kopyalayın. Görüntüleme (F3), dışarı
kopyalama, arama (Alt+F7) ve karşılaştırma her klasörde olduğu gibi çalışır; kökten ".." paket dosyasına geri götürür.

- **.deb** (Enter; `.udeb`, `.ddeb` de): paketin üyeleri (`debian-binary`, `control.tar.*`,
  `data.tar.*`); iki tar üyesi dosyalarından oluşan klasörlerdir (gzip, xz, zstd, bzip2 ya da sıkıştırılmamış),
  kipleri, zamanları ve bağlantılarıyla.
- **.rpm** (Enter): yükün (gzip, xz, zstd veya bzip2 ile sıkıştırılmış bir cpio arşivi) dosyaları, kurulu
  yollarında, boyutları, kipleri ve zamanlarıyla. Eski LZMA biçimiyle sıkıştırılmış yükler ve
  4 GB veya daha büyük dosyalı paketler bir iletiyle reddedilir.
- **.cab** (Enter): bir Microsoft Cabinet'in dosyaları klasörleri, zamanları ve öznitelikleriyle. Depolanmış, MSZIP ve
  LZX klasörleri açılır; Quantum klasörleri (çok eski cabinet'ler) listelenir ancak dosyaları bir
  iletiyle açılmayı reddeder. Bir setin sonraki cabinet'inde devam eden dosyalar dışarıda bırakılır.
- **.msi / .msm** (Ctrl+PgDn: Enter yine yükleyiciyi başlatır): paketin içindeki cabinet'lerin dosyaları,
  kurulacakları yollarda (File, Component ve Directory tablolarından; bir sistem klasörü
  adını korur, örneğin `ProgramFilesFolder/My App/app.exe`). Tabloların adlandırmadığı bir dosya cabinet'in adı altında, cabinet içindeki
  adıyla görünür. Yükleyicinin paketin yanından aldığı dosyalar burada yoktur.
- **Konteyner imajı arşivleri** (`docker save -o app.tar` ya da bir tar içinde OCI imaj düzeni): böyle bir dosya, programın arşiv olarak açtığı
  bir `.tar` dosyasıdır (Enter ve Ctrl+PgDn). Onun içinde *Eklentiler > Kapsayıcı görüntüsü*, aynı
  dosyayı imajlar olarak açar: her imaj için bir klasör (`example/app:1.0` için `example_app_1.0`), içinde `config.json`, her katman için bir
  klasör (`layer 1 (3f4ca61aafcd)`, sıkıştırılmış katmanlar açıldığında açılır) ve `merged`: katmanların birlikte oluşturduğu dosya
  sistemi, sonraki katmanlar öncelikli olarak, whiteout'lar (`.wh.name`) ve opaque klasörler
  (`.wh..wh..opq`) uygulanarak. Yalnızca kökünde `manifest.json` ya da `index.json` ve `oci-layout` bulunan bir `.tar`
  bunu sunar.
- **Pakette Lister (F3):** bir .deb veya .rpm'in adı, sürümü, mimarisi, açıklaması, bağımlılıkları (ve
  bir rpm'in sağladıkları) ve .deb'in control dosyaları; bir cabinet'in klasörleri sıkıştırması, blokları ve
  boyutlarıyla; bir .msi'nin ürün adı, sürümü, üreticisi, ürün ve yükseltme kodları ve cabinet'leri; bir imaj
  arşivinin imajları etiketleri, platformu, entrypoint'i, komutu, ortamı, portları, etiketleri ve katmanlarıyla (boyut,
  sıkıştırma ve her birini oluşturan komut).
- **Boyut:** sıkıştırılmış parçalar ilk açıldığında bir kez açılır; belleğe ya da 32 MB'tan büyükse geçici bir dosyaya
  açılır, dosya paket kapandığında (son kullanımından bir dakika sonra) silinir. Bir .msi bütünüyle belleğe okunur.

## Görüntüleyiciler

Görüntüleyiciler eklentisi NextCommander ile birlikte gelir ve Lister'a (F3) ile Hızlı görünüm'e görünümler ekler. Kendi dosyaları için
kendiliğinden açılırlar (resim üstverisi görünümü hariç: o, resmin yanındadır); 1 tuşu düz metni
(3 tuşu baytları) eskisi gibi gösterir.

| Dosyalar | Görünüm |
|---|---|
| `.csv`, `.tsv`, `.tab` | Bir tablo: ayırıcı kendiliğinden bulunur (virgül, noktalı virgül, sekme, dikey çizgi), tırnak içindeki alanlar ve içlerindeki satır sonları okunur, ilk satır başlıktır. Bir sütun başlığına tıklamak o sütuna göre sıralar (sayılar sayı olarak); sütunlar yeniden boyutlandırılabilir ve taşınabilir. |
| `.json`, `.geojson`, `.har`, `.webmanifest`, `.xml`, `.plist`, `.gpx`, `.kml`, `.resx`, `.yaml`, `.yml` | Katlanabilen bir ağaç, değerler türüne göre renklendirilmiş, ilk iki düzey açık. Okunamayan bir dosya nerede olduğunu söyler, sonra metnini gösterir. Proje ve kod dosyaları (`.csproj`, `.xaml`, `.config` ...) ile `.svg` eskisi gibi kalır. |
| `.eml`, `.msg` (Outlook) | E-posta: gönderen, alıcılar, tarih, konu, ekler ve e-postanın kendi resimleriyle gövde. İnternetteki resimler yüklenmez (göndericiye e-postanın okunduğunu bildirebilirler) ve betikler dışarıda bırakılır. |
| `.epub` | Kitap: başlık, yazar, yayıncı, kapak, içindekiler ve resimleriyle bölümler. |
| `.ipynb` | Not defteri: Markdown hücreleri, numarası ve çıktılarıyla (metin, tablolar, resimler, hatalar) kod. |
| `.cer`, `.crt`, `.der`, `.pem`, `.p7b`, `.pfx`, `.p12` | Yapraktan köke her sertifika: konu, veren, geçerlilik (ve şu anda geçerli olup olmadığı), anahtar, algoritmalar, parmak izleri, kullanımlar ve alternatif adlar. Parolalı bir `.pfx` bunu söyler. Hiçbir şey kurulmaz. |
| `.exe`, `.dll`, `.sys`, `.ocx`, `.scr`, `.cpl`, `.efi` | Sürüm bilgisi, mimari, tür (pencere, konsol, sürücü, kitaplık; .NET), derleme zamanı, bölümler ve onu kimin imzaladığı. İmza gösterilir, denetlenmez; Windows'un kendi dosyaları çoğu zaman bir katalogda imzalanır. |
| `.ttf`, `.otf`, `.ttc`, `.woff`, `.woff2` | Yazı tipi: aile, stil, ağırlık, glifler, altı boyutta örnek satırlar (Türkçe harflerle) ve karakterlerinin bir tablosu; dosyadan çizilir (hiçbir şey kurulmaz). |
| `.vcf`, `.ics` | Kişiler kartlar olarak (fotoğraf, telefonlar, e-postalar, adresler, notlar); etkinlikler zaman sırasına göre yerleri, düzenleyeni, katılımcıları ve tekrarıyla. |
| `.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`, `.heic`, `.heif`, `.webp`, `.gif`, `.bmp`; kamera RAW: `.cr2`, `.cr3`, `.nef`, `.nrw`, `.arw`, `.orf`, `.rw2`, `.dng`, `.raf`, `.pef`, `.srw` | Resmin üstverisi, **resmin yanında**: Lister'ın gösterebildiği bir resim yine resim olarak açılır (4 tuşu), 6 tuşu üstverisini gösterir (HEIC ve Lister'ın çizemediği RAW dosyaları onda açılır). Bir özet (kamera, lens, çekim tarihi dilinizin biçiminde, pozlama, diyafram, ISO, odak uzaklığı, flaş, resim boyutu, yönlendirme, renk uzayı); konum (ondalık derece ile derece, dakika, saniye olarak enlem ve boylam, yükseklik, yön, GPS zamanı ve yerin tarayıcıda açılan bağlantı olarak OpenStreetMap adresi); açıklama (IPTC / XMP başlık, altyazı, anahtar sözcükler, oluşturan, telif hakkı, XMP derecelendirme ve etiket); diğer EXIF etiketleri; sonra dosyanın içerdiği her dizin ve etiket, her biri kimliğiyle bir tabloda. EXIF, IPTC veya XMP içermeyen bir resim bunu söyler. |

![Lister'da resim üstverisi](../screenshots/picture-metadata.png)

## GitHub ve GitLab depoları

Depolar eklentisi NextCommander ile birlikte gelir ve GitHub ile GitLab üzerindeki depolara klonlamadan, web API'leri üzerinden göz atar. GitHub Enterprise Server ile
kendi GitLab sunucunuzla da çalışır (adresleri ayardır) ve programın proxy'si üzerinden gider (*Yapılandırma > İşlem > Proxy*).

- **GitHub depoları** ve **GitLab depoları**, kayıtlı bağlantılarla birlikte listelenir (Bağlantı > Kayıtlı
  bağlantılar ve düğme çubuğundaki bağlantılar düğmesi). `github:///` ve `gitlab:///` adreslerini açarlar: kendi
  depolarınız (erişim belirteciyle: sahibi olduklarınız, katkıda bulunduklarınız ya da bir kuruluş aracılığıyla eriştikleriniz; GitLab: üyesi olduğunuz
  projeler), ayarlarda adı geçenler ve bu çalıştırmada açılanlar, `owner⁄name` olarak
  (GitLab `group⁄subgroup⁄project`).
- **Başka bir depo**, yol satırında yoluyla açılır: `github://dotnet/runtime` ya da
  `gitlab://gitlab-org/gitlab` (GitLab alt grubunun eğik çizgileri `|` olarak: `gitlab://group|sub/project`), veya kökte
  Eklentiler menüsünün (ve dosya listesinin kendi menüsünün) **Bir depo aç...** seçeneğiyle: `owner/name` yazın ya da
  deponun adresini yapıştırın; sonrasında kökte listelenir. `github://owner/` bir sahibin depolarını listeler
  (bir GitLab grubu: alt gruplarını ve projelerini).
- **Bir depo**, **branches**, **tags** ve **releases** klasörlerini ve varsayılan dalının dosyalarını gösterir.
  Her dal ve etiket dosyalarını o andaki hâliyle içerir (addaki eğik çizgi `⁄` olarak gösterilir: `feature⁄login`); her
  sürüm, varlıklarının (asset) bir klasörüdür (GitLab: sürümün bağlantıları). Her klasörde olduğu gibi görüntüleyin (F3), karşılaştırın ve dışarı kopyalayın;
  bir dosyanın zamanı commit'inin zamanıdır. Git LFS ile tutulan bir dosya işaretçisini gösterir. Bir alt modül (submodule) dışarıda bırakılır, varsayılan dalın
  üç klasörden biri gibi adlandırılmış üst düzey klasörü de (onu `branches/<default branch>` altında açın).
- **Salt okunur:** sunucuya hiçbir şey yazılmaz.
- **Tarayıcıda aç** (dosya listesinin sağ tık menüsü) bir deponun, klasörün, dosyanın veya sürümün sayfasını açar.
- **Depoyu klonla...** (Eklentiler menüsü, bir depo içindeyken ya da biri seçiliyken) bir klasör sorar ve orada yeni bir klasöre
  kurulu `git clone` komutunu çalıştırır (içinde bulunduğunuz dal veya etiket çıkış yapılır). Erişim belirteci
  yalnızca bu tek komut için git'e verilir (ortam üzerinden, klonda saklanmaz). Eklenti diğer panelin klasörünü
  öğrenemez; bu yüzden klasör sorulur, son kullanılan yeniden önerilir.
- **Ayarlar** (*Yapılandırma > Eklentiler > GitHub / GitLab depoları*): GitHub API adresi
  (`https://api.github.com` ya da GitHub Enterprise için `https://<server>/api/v3`), GitHub erişim belirteci,
  listelenecek GitHub depoları, GitLab adresi (`https://gitlab.com`), GitLab erişim belirteci (kapsam
  `read_api`), listelenecek GitLab projeleri. Belirteçler parola deposunda tutulur (varsa ana parola ile),
  hiçbir zaman ayar dosyasında değil, ve yalnızca bir istek gerektirdiğinde sorulur.
- **Hatalar ne yapılacağını söyler:** API'nin istek sınırı (GitHub belirteçsiz saatte 60, belirteçle 5000 isteğe izin verir)
  sıfırlanma zamanıyla bildirilir; belirteçsiz bulunamayan bir depo belirteç gerektiren özel bir depo olabilir;
  reddedilen bir belirteç ve ulaşılamayan bir sunucu (ağ, proxy) olarak adlandırılır.
- **Sınırlar:** bir listenin ilk 3000 deposu, dalı veya etiketi gösterilir ve en yeni 500 sürüm. GitHub'ın kısaltarak verdiği çok büyük bir ağaç
  (ya da 2000 girişi aşan bir GitLab ağacı) klasör klasör okunur. GitLab'ın ağacı boyut vermez: bir dosyanın
  boyutu açıldığında veya kopyalandığında sorulur ve listede 0 görünür; GitLab sürüm bağlantılarının boyutu yoktur.
  Sürüm kaynak arşivleri (zip / tar.gz) listelenmez.

## Eklentileri kullanma

### Kurulum

Eklenti, bir `plugin.json` ve eklentinin derlemesini içeren bir klasördür. Klasörü iki eklenti
klasöründen birine koyun ve NextCommander'ı yeniden başlatın:

| Klasör | Ne için |
|---|---|
| [Ayarlar klasörü](settings-folder.md)ndeki `plugins` (`%APPDATA%\NextCommander\plugins`, `~/.config/NextCommander/plugins`, `~/Library/Application Support/NextCommander/plugins`) | Kendi eklentileriniz. Program güncellemelerinden etkilenmez. |
| Programın yanındaki `plugins` | NextCommander ile gelen eklentiler (rclone). Kurulum veya güncelleme bu klasörün yerini alır. |

```text
plugins/
  hello/
    plugin.json
    Hello.Plugin.dll
    Hello.Plugin.deps.json      (when the plugin has dependencies)
    SomeLibrary.dll             (the plugin's own dependencies)
```

`plugin.json` içeren her alt klasör bir eklentidir; diğer klasörler yok sayılır.

### Eklentileri açma ve kapatma

*Yapılandırma > Seçenekler > İşlem > Eklentiler*, kurulu her eklentiyi bir onay kutusuyla listeler. İşareti kaldırılan eklentiler
yüklenmez; değişiklik yeniden başlatınca geçerli olur. Artık kurulu olmayan bir eklenti, yeniden işaretleyene kadar
listede işaretsiz olarak kalır (liste `DisabledPlugins` ayarıdır).

### Bir eklentinin ayarları

Ayarları olan bir eklentinin aynı sayfada (*Yapılandırma > Seçenekler > İşlem > Eklentiler*) kendi grubu vardır; başlığı
adıdır; yapılandırma araması bunları da bulur. Eklentiler penceresindeki **Yapılandır...** düğmesi
yapılandırmayı orada açar; bir eklenti kullanılamadığında verdiği ileti de açar (örneğin
programı bulunamadığında). Ayarlar Uygula / Tamam ile geçerli olur; ayar aksini söylemedikçe yeniden başlatma gerekmez;
eklentinin kullanamayacağı bir değer (var olmayan bir program) düğmelerin yanında nedeniyle birlikte reddedilir.

| Eklenti | Ayarlar |
|---|---|
| Git | git programı; Git sütunlarının bir deponun durumunu ne sıklıkla okuduğu; geçmişin kaç commit listelediği |
| Docker | docker programı (podman da çalışır); komutların bir konteynerin içinde çalıştığı kullanıcı; volume açmak için kullanılan imaj |
| Kubernetes | kubectl programı; kubeconfig dosyası; Pod günlükleri'nin gösterdiği günlük satırı sayısı |
| rclone | rclone programı; uzak bağlantıları içeren rclone.conf |
| SQLite | SQLite kitaplığı (yeniden başlatınca); F3 özetinde her tablonun satırları |

Boş bir program yolu eskiden olduğu gibi bir anlam taşır: ortam değişkeni (`FM_GIT`, `FM_DOCKER`, `FM_KUBECTL`,
`FM_RCLONE`, `FM_SQLITE`), sonra PATH ve olağan klasörler. Değerler ayar dosyasında eklentinin adı altında tutulur
(`PluginSettings`); varsayılanında bırakılan bir değer saklanmaz.

### Eklentiler menüsü

Düğme çubuğundaki **Eklentiler** düğmesi (bağlantı yöneticisinin yanında), eklentilerin sunduğu yerleri listeler;
örneğin *rclone > rclone uzak bağlantıları*; en sonda eklentiler penceresi olan **Eklentiler...** bulunur. Düğme yalnızca
en az bir eklenti yüklüyken görünür; *Yapılandırma > Seçenekler > Düzen > Düğme çubukları > Eklentiler düğmesi* onu kalıcı olarak gizler. Komut
`nc_PluginsMenu` (bir kısayol veya komut satırı için); `nc_Plugins` eklentiler penceresini açar.

### Eklentiler penceresi

**Eklentiler...**, kurulu her eklentiyi listeler: adı, sürümü ve açıklaması, durumu (*Açık*, *Kapalı*,
*Hata* ya da başlangıçtan beri değiştirildiyse *Yeniden başlatınca açılacak / kapanacak*), ne eklediği (dosya sistemleri, sütun
sağlayıcılar, paketleyiciler, görüntüleyiciler) ve klasörü.
- **Ara** (Ctrl+F) ada, türe veya klasöre göre; **Tümü**, **Dosya sistemleri**, **Sütun sağlayıcılar** ve
  **Kapalı** çipleri listeyi süzer ve her birinde kaç eklenti olduğunu gösterir.
- Bir eklentide: **yolunu kopyala**, **klasörünü aç** (panelde), **yapılandır** (ayarları olan bir eklenti, bkz.
  [Bir eklentinin ayarları](#a-plugins-settings)) veya **aç / kapat** (*Yapılandırma > Eklentiler* ile aynı). Klasörü
  silinmiş, kapatılmış bir eklenti listeden kaldırılabilir.
- **+**, bir zip dosyasından (`plugin.json` en üstte ya da tek bir klasörün içinde) veya bir
  `plugin.json` dosyasından eklenti kurar: ayarlar klasörünün eklenti klasörüne, adı altında kopyalanır; orada eski bir kopya
  varsa değiştirilir (yüklü bir eklentiyi değiştirmeden önce kapatıp yeniden başlatın, dosyaları kullanımdadır).
- Eklentiler başlangıçta yüklenir: bir değişiklikten sonra pencere **Şimdi yeniden başlat** önerir.
- **Aç** (Enter ya da çift tıklama), eklentinin bir yeri varsa onu (rclone uzak bağlantıları ...), yoksa klasörünü açar.

### Bir eklenti yüklenmediğinde

Eklentiler penceresi böyle bir eklentiyi *Hata* olarak işaretler; nedeni durumun ipucu balonundadır, örneğin:

| İleti | Anlamı |
|---|---|
| `plugin.json: "Assembly" is missing.` | Manifest hiçbir derleme adlandırmıyor. |
| `Assembly not found: ...` | `Assembly` içinde adı geçen dosya klasörde yok (ya da klasörün dışını gösteriyor, buna izin verilmez). |
| `No file system, column, packer or viewer found in the plugin.` | Derlemede, parametresiz yapıcısı olan ve arayüzlerden birini uygulayan herkese açık bir sınıf yok. |
| Tür yükleme ya da eksik yöntem hatası | Eklenti NextCommander'ın başka bir sürümüne göre derlenmiş. Sizin sürümünüze göre derlenmiş birini edinin. |

Program günlüğü (ayarlar klasöründe `logs/nextcommander-<date>.log`) her eklenti için bir satır içerir: ne eklediği
ya da neden yüklenmediği.

### Güvenlik

Bir eklenti NextCommander'ın içinde sizin haklarınızla çalışır: dosyalarınızı okuyabilir ve değiştirebilir, ağı kullanabilir. Eklentileri
yalnızca güvendiğiniz kişilerden kurun. Eklentiler dışa aktarılan yapılandırmanın (*Ayarları dışa aktar*) parçası değildir; `plugins`
klasörünü kendiniz kopyalayın.

## Eklenti yazma

### Gerekenler

- NextCommander'ın derlendiği .NET SDK (bkz. `global.json`; .NET 10).
- NextCommander kaynak kodu (bu depo): eklenti `NextCommander.Core`'a karşı derlenir. Yayımlanan
  program tek dosyadır, bu yüzden başvurulacak bir `NextCommander.Core.dll` yoktur.

### Proje

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- writes Hello.Plugin.deps.json, which the loader uses to find the plugin's own dependencies -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- compiled against NextCommander.Core, but the application's copy is used at run time: do not copy it -->
    <ProjectReference Include="..\NextCommander\src\NextCommander.Core\NextCommander.Core.csproj" Private="false" />
  </ItemGroup>
</Project>
```

`Private="false"`, `NextCommander.Core.dll` dosyasını eklentinin çıktısının dışında tutar. Kendi NuGet paketleriniz eklentinin yanına kopyalanır ve
onun klasöründen yüklenir. `NextCommander.Core`'un kendi paketleri (`Blake3.dll` gibi) da çıktıda
görünebilir; programın kopyaları kullanılır, bu yüzden bırakılabilirler.

### plugin.json

```json
{
  "Name": "hello",
  "Assembly": "Hello.Plugin.dll",
  "Description": "Two read-only files at hello://",
  "Version": "1.0"
}
```

| Alan | |
|---|---|
| `Name` | Eklentinin Eklentiler listesindeki ve *Yapılandırma > Eklentiler* içindeki adı (kapatılması bu adla saklanır). Yoksa klasör adı kullanılır. |
| `Assembly` | Ana derleme, klasöre göre göreli. Zorunludur. |
| `Description` | İsteğe bağlı. |
| `Version` | İsteğe bağlı; Eklentiler listesinde adın yanında gösterilir. |

### Nasıl yüklenir

1. Başlangıçta NextCommander iki eklenti klasöründeki her `plugin.json` dosyasını okur (önce ayarlar klasörü) ve
   *Yapılandırma > Eklentiler* içinde kapatılmış eklentileri atlar.
2. Ana derleme kendi yükleme bağlamına yüklenir. Bağımlılıkları eklentinin klasöründen gelir (`.deps.json` aracılığıyla,
   yerel kitaplıklar da); `NextCommander.Core`, `System.*` ve programın daha önce yüklediği her derleme (örneğin
   Avalonia) programla paylaşılır. Bu yüzden eklentinin türleri programın arayüzlerini uygular ve
   bir eklenti bunların kendi kopyasını getiremez.
3. `IFileSystemProvider`, `IColumnProvider`, `IPackerPlugin` veya `IViewerPlugin` arayüzünü uygulayan her
   **herkese açık, soyut olmayan, parametresiz yapıcılı sınıf** bir kez oluşturulur. Bir sınıf birkaçını birden uygulayabilir.
4. Dosya sistemleri programın dosya sistemlerine, sütunlar sütun listesine, paketleyiciler Paketle penceresine, görüntüleyiciler
   Lister'a katılır.
5. Program sona erdiğinde `IDisposable` uygulayan dosya sistemleri serbest bırakılır (arka plan işlemlerinizi ve
   bağlantılarınızı orada durdurun).

Yapıcı başlangıçta, ana pencereye giderken çalışır: hızlı tutun. Programları arayın, yapılandırma
dosyalarını okuyun ve bağlantıyı ilk çağrı geldiğinde, daha sonra kurun.

## Dosya sistemleri

Bir dosya sisteminin bir **şeması** (`hello`) vardır ve bu şemayla başlayan yolları işler. Yollar `VfsPath` değerleridir:
`VfsPath.Create("hello", "host", "/folder/file.txt")` `hello://host/folder/file.txt` olarak gösterilir; `Authority`,
`//` sonrasındaki kısımdır (bir sunucu, bir hesap, bir aygıt; boş olabilir) ve `Path` her zaman `/` ile başlar.

```csharp
using System.Text;
using NextCommander.Core.Vfs;

namespace Hello.Plugin;

/// <summary>hello:/// with two read-only files.</summary>
public sealed class HelloFileSystem : IFileSystemProvider
{
    private static readonly string[] s_names = ["a.txt", "b.txt"];

    public string Scheme => "hello";

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        directory.Path == "/"
            ? Task.FromResult<IReadOnlyList<VfsEntry>>([.. s_names.Select(n => Entry(directory, n))])
            : throw new DirectoryNotFoundException(directory.ToString());

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) =>
        Task.FromResult(path.Path == "/" ? new VfsEntry { Name = "hello", Path = path, IsDirectory = true }
            : s_names.Contains(path.Name) ? Entry(path.Parent!, path.Name) : null);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("hello from " + path.Name)));

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;

    private static VfsEntry Entry(VfsPath folder, string name) => new()
    {
        Name = name,
        Path = folder.Combine(name),
        Size = Encoding.UTF8.GetByteCount("hello from " + name),
        LastWriteTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local),
    };
}
```

Bir panelde görmek için yol satırına `hello:///` yazın (ya da [Eklentiler menüsündeki bir yerden](#places-in-the-plugins-menu) açın).

### Üyeler

| Üye | NextCommander'ın beklediği |
|---|---|
| `Scheme` | Küçük harf, benzersiz. Programın zaten işlediği bir şema (`file`, `archive`, `sftp`, `ftp`, `s3`, `rclone` ...) eklentiye hiç ulaşmaz: yerleşik dosya sistemleri önce gelir. |
| `Capabilities` | Dosya sisteminin neler yapabildiği: `Read`, `Write` (yeni dosyalar ve klasörler, silme), `Rename` (dosya sistemi içinde `MoveAsync`), `SetTimes`, `Watch`, `Links`, `Trash`. Eksik bir yeteneğe ihtiyaç duyan komutlar sunulmaz ya da dosya sisteminin bunu yapamadığını bildirir. |
| `CanHandle` | Varsayılan: şema eşleşir. Yalnızca bir şemanın bir kısmını işlemek için geçersiz kılın (override). |
| `ListAsync` | Bir klasörün girişleri, `.` ve `..` olmadan. Var olmayan bir klasör için `DirectoryNotFoundException` fırlatın. |
| `GetEntryAsync` | Tek bir giriş, ya da yoksa **null** (istisna değil). Kök (`/`) için bir klasör girişi döndürün. |
| `OpenReadAsync` | Okunabilir bir akış. Aranabilir olması gerekmez; Lister ve kopyalamalar onu baştan okur. |
| `OpenWriteAsync` | Yeni veya değiştirilen bir dosya için yazılabilir bir akış. Hedef önce uzunluğu gerektiriyorsa (bir HTTP PUT, bir aygıt), bir `UploadOnCloseStream` döndürün: verileri geçici bir dosyada toplar ve akış kapandığında yükler. `overwrite: false` ve var olan bir dosya varsa `IOException` fırlatın. |
| `CreateDirectoryAsync` | Klasörü ve eksik üst klasörlerini oluşturun. |
| `DeleteAsync` | Bir dosya ya da boş klasör; `recursive: true` ile tüm bir klasör ağacı. |
| `MoveAsync` | Dosya sistemi içinde yeniden adlandırma veya taşıma. Veriyi kopyalamadan yapılamıyorsa (başka bir hesap, başka bir sürücü) `CrossDeviceMoveException` fırlatın: program o zaman kopyalar ve siler. |
| `SetTimesAsync` | Kopyalamadan sonra zamanları ayarlayın; dosya sistemi yapamıyorsa hiçbir şey yapmayın (ve `SetTimes` değerini `Capabilities` dışında bırakın). |
| `Watch` | İsteğe bağlı: klasör değiştiğinde `onChanged` çağırın ve döndürülen nesne serbest bırakıldığında durun. Panel o zaman kendiliğinden yenilenir. Yapamıyorsanız null döndürün (varsayılan). |

`VfsEntry`; `Name`, `Path`, `IsDirectory`, `Size`, `LastWriteTime` (yerel saat), isteğe bağlı `CreationTime`,
`LastAccessTime`, `Attributes` (gizli, salt okunur ...), `UnixMode`, `IsLink` ve `LinkTarget` alanlarını içerir.

### Hatalar

Program, istisnanın `Message` değerini kullanıcıya gösterir; bu yüzden kullanıcının anlayabileceği iletiler yazın. Şu türleri kullanın:

| İstisna | Ne zaman | Program ne yapar |
|---|---|---|
| `IOException` | Ters giden her şey: sunucuya ulaşılamıyor, hizmet reddetti, veri bozuk. | Klasör açarken: bir ileti, panel bulunduğu yerde kalır. Kopyalama veya silmede: iletiyle birlikte hata sorusu (yeniden dene / atla / iptal). |
| `DirectoryNotFoundException`, `FileNotFoundException` | Klasör ya da dosya yok. | Yukarıdaki gibi. |
| `UnauthorizedAccessException` | Bu klasör veya dosya için izin yok. | Ulaşılabilir bir sunucunun okunmaması gereken klasörü `<Read error!>` gösterir; bir işlemde: hata sorusu. |
| `NotSupportedException` | Dosya sistemi bunu hiç yapamaz. | Bir ileti. |
| `CrossDeviceMoveException` | Hesaplar veya sürücüler arasında `MoveAsync`. | Bunun yerine kopyala ve sil. |
| `OperationCanceledException` | Kullanıcı iptal etti. | Hiçbir şey: uzun çağrılarda `cancellationToken` değerine uyun. |

Kullandığınız kitaplıkların hatalarını (`HttpRequestException`, `SocketException`, JSON hataları, bir
SDK'nın hataları) bu türlerden birine çevirin; genellikle özgün hata iç istisna olarak `IOException`. Diğer istisna türleri bir dosya sisteminden
beklenmez ve beklenmeyen bir hata olarak karşınıza çıkabilir. rclone eklentisinin `ErrorOf` yöntemi bir örnektir: API'nin yanıtlarını
`FileNotFoundException`, `UnauthorizedAccessException` veya `IOException` türlerine eşler.

### İş parçacıkları

Her yöntem bir arka plan iş parçacığından ve birkaçı aynı anda çağrılabilir (bir kopyalama birkaç dosya okur,
panel listelerken bir sütun sağlayıcı okur). Bir dosya sisteminden kullanıcı arayüzüne dokunmayın ve kendi
paylaşılan durumunuzu (bir bağlantı, bir önbellek) bir kilit ya da `SemaphoreSlim` ile koruyun.

### İsteğe bağlı arayüzler

| Arayüz | Kullanım |
|---|---|
| `IUploadTimeSupport` | Hizmet değiştirilme zamanını yalnızca yüklemeyle birlikte alabilir: `PrepareUpload(path, time)`, `OpenWriteAsync` öncesinde çağrılır. |
| `IVolumeSpaceProvider` | `GetSpaceAsync(path)`: boyut ve boş alan, panelin alt bilgisinde bir sürücününki gibi gösterilir. |
| `ILinkSupport`, `IAttributeSupport` | Sembolik bağlantı oluşturma; öznitelikleri ve Unix izinlerini ayarlama. |
| `IContainerProvider` | Klasör gibi açılan dosyalar (bir arşiv biçimi): `CanOpen(file, force)` karar verir, `GetRoot(file)` arşivin kök klasörünün yolunu sizin şemanızda döndürür. `force`, Ctrl+PgDn için doğrudur (yalnızca istek üzerine açılan biçimler). |
| `IFlushableProvider` | Bellekte tutulup sonra yazılan değişiklikler: `FlushAsync` her işlemden sonra ve program sona erdiğinde çağrılır. |
| `IDisposable` | Program sona erdiğinde çağrılır. |

## Eklentiler menüsündeki yerler

Bir dosya sistemi, `IPluginEntryPoint` (`NextCommander.Core.Plugins`) arayüzünü de uygulayarak bir başlangıç noktası sunabilir. Eklentiler menüsünde
grubunun altında görünür:

```csharp
public sealed class HelloFileSystem : IFileSystemProvider, IPluginEntryPoint
{
    // ... the file system ...

    public string EntryName => "Hello files";

    public string EntryGroup => "hello";

    public VfsPath EntryRoot => VfsPath.Create("hello", string.Empty, "/");

    // null: the place can be opened; otherwise the message shown instead (a missing program, no configuration ...)
    public string? Unavailable => null;
}
```

Girişi seçmek `EntryRoot` yolunu etkin panelde açar, ya da null değilse `Unavailable` iletisini gösterir. rclone
eklentisi bunu, rclone eksik olduğunda nasıl kurulacağını açıklamak için kullanır.

Etkin panelin klasörüne bağlı bir yer bunun yerine `IPluginFolderEntry` uygular: `RootFor(folder)`
açılacak klasörü döndürür, giriş uygun değilse null döndürür. Menü her açıldığında çağrılır; bu yüzden hafif tutun
(Git eklentisi yukarıda bir `.git` klasörü arar). Bu tür girişler Eklentiler menüsünde ilk sırada gelir.

```csharp
public string EntryName => "Git history";

public string EntryGroup => "Git";

public VfsPath? RootFor(VfsPath folder) =>
    folder.IsLocal && RepositoryOf(folder.Path) is { } repository ? RootOf(repository) : null;
```

Etkin panelin klasörü ve seçili öğeleri için eylemler `IPluginFolderActions` ile gelir: `ActionsFor(folder,
selected, dialogs)`, `PluginAction` değerleri döndürür (ad, simge, ileti ya da null veren `RunAsync`; klasör
sonrasında yeniden okunur). Bir menü açıldığında çağrılır; bu yüzden hafif tutun. `IPluginDialogs` programın iletişim kutularını ödünç verir:
`PromptAsync`, `ChooseAsync` (bir listeden biri), `ConfirmAsync` ve `ShowTextAsync(fileName, text)` (Lister; bir
`.diff` adı farkı renklendirir). Git eklentisinin `GitActions` sınıfı bir örnektir.

`IOriginProvider` ile, dosya sisteminin bir kökünden ".." geldiği yere geri götürür: `OriginOf(root)`
o klasörü ve imlecin üzerine konacağı adı döndürür (ya da null).

## Sütunlar

Bir sütun sağlayıcı, kullanıcının bir görünüm moduna ekleyebileceği alanlar sunar (*Göster > Görünüm > Özel görünüm modları > Görünüm modlarını düzenle...*).

```csharp
using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;

public sealed class ReversedNameColumns : IColumnProvider
{
    public string Id => "sample";

    public string DisplayName => "Sample plugin";

    // field ids are "<provider id>.<field>"
    public IReadOnlyList<ColumnField> Fields { get; } = [new("sample.reversed", "Reversed name")];

    public bool Supports(VfsEntry entry) => !entry.IsDirectory;

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds,
        ProviderRegistry registry, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["sample.reversed"] = new string(entry.Name.Reverse().ToArray()) });
}
```

- `Supports` her satır için çağrılır: hafif tutun (uzantı, klasör olup olmadığı). `GetValuesAsync` arka planda
  çalışır; kullanılamayan bir değer için null döndürün.
- Bir alanın `ColumnKind` değeri (`Text`, `Number`, `Size`, `Date`), değerin ne tür olduğunu söyler (boyut
  boyut biçiminde, tarih tarih biçiminde gösterilir).
- Dosyayı okumak için `registry.Resolve(entry.Path).OpenReadAsync(entry.Path, cancellationToken)` kullanın: bu, yalnızca yerel diskte değil,
  her dosya sisteminde çalışır.

## Paketleyiciler ve görüntüleyiciler

### Paketleyiciler (Alt+F5)

```csharp
public sealed class CpioPacker : IPackerPlugin
{
    public string FormatName => "cpio";

    public string Extension => "cpio";

    public bool SupportsPassword => false;

    public async Task PackAsync(IReadOnlyList<PackSource> sources, Stream output, string? password,
        IProgress<long> progress, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            // source.Name uses '/' and is relative to the archive root; folders have IsDirectory
            if (!source.IsDirectory)
            {
                await using var data = await source.OpenAsync(cancellationToken);
                // ... write a header and copy data to output, reporting the bytes read
            }
        }
    }
}
```

Biçim, Paketle penceresinin paketleyicileri arasında görünür. Bu tür arşivleri okumak ayrı bir iştir: aynı eklentide bir
`IContainerProvider` dosya sistemi.

### Görüntüleyiciler (Lister, F3)

```csharp
public sealed class LogViewer : IViewerPlugin
{
    public string Name => "Log viewer";

    // the file name and its first bytes (at most 4 KB)
    public bool CanView(string fileName, ReadOnlySpan<byte> header) => fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new ViewerContent { Html = $"<pre>{System.Net.WebUtility.HtmlEncode(text)}</pre>", Status = "Log" };
    }
}
```

`ViewerContent`; HTML (belge önizlemesi gibi gösterilir), düz `Text` ya da kendi Avalonia `Control` nesneniz ile birlikte
bir `Status` satırı içerir. `dark`, programın koyu tema gösterip göstermediğini söyler. `Control` döndüren bir görüntüleyici
Avalonia'ya başvurur: NextCommander ile aynı Avalonia sürümünü kullanın (`Directory.Packages.props`) ve kopyalamayın
(`Private="false"`); programın kopyası kullanılır. `CanView` değeri evet diyen ilk görüntüleyici dosyayı alır; Lister
onu eklenti modunda açar (6 tuşu). Lister'ın kendi görünümünün yanına bir görünüm ekleyen görüntüleyici `IsDefaultView => false` ayarlar:
bir resim o zaman resim olarak açılır ve görüntüleyici onun 6 tuşudur.

## Ayarlar

Bir eklenti kendi ayar penceresini çizmez: neyin ayarlanabileceğini söyler, program bunu yapılandırmanın Eklentiler sayfasında
kendi denetimleriyle gösterir (eklenti başına bir grup, programın görünümü, çevirileri,
araması, Uygula / Tamam ve İptal ile), değerleri eklentinin adı altında saklar ve eklentiye verir.

```csharp
public sealed class HelloSettings : IConfigurablePlugin
{
    public string SettingsTitle => "Hello";

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("program", PluginSettingKind.Program, "hello program") { Description = "Empty: hello on the PATH." },
        new("greeting", PluginSettingKind.Text, "Greeting") { Default = "Hello" },
        new("rows", PluginSettingKind.Number, "Rows") { Default = "10", Min = 1, Max = 100 },
        new("mode", PluginSettingKind.Choice, "Mode") { Default = "fast", Choices = [("fast", "Fast"), ("safe", "Safe")] },
    ];

    // OK / Apply are refused while this says something (shown next to the buttons)
    public string? Validate(IPluginSettingValues values) =>
        values.Get("program") is { } program && !File.Exists(program) ? $"The program was not found: {program}" : null;

    // at the start and after every change (no restart)
    public void Apply(IPluginSettingValues values) => HelloTool.Program = values.Get("program");
}
```

- **Nerede:** eklentinin parametresiz yapıcılı herhangi bir herkese açık sınıfı, kendi ayar sınıfı ya da
  dosya sistemlerinden, sütunlarından, görüntüleyicilerinden biri (ikisi için aynı örnek kullanılır). Eklenti başına bir `IConfigurablePlugin`.
- **Türler:** `Text`, `Number` (`Min`, `Max`), `Toggle` ("true" / "false"), `Choice` (`Choices`: değer ve metin),
  `Program` ve `File` (Gözat düğmesiyle), `Folder` (klasör seçiciyle), `Secret` (gizli yazılır, parola deposunda tutulur; aşağıya bakın).
- **Değerler** metindir: `values.Get(key)` değeri ya da ayarın `Default` değerini verir (boşsa null);
  `GetBool` ve `GetNumber` onları bu türde okur. Varsayılana eşit bir değer saklanmaz.
- **Uygulama:** `Apply`, eklenti yüklendiğinde (herhangi bir şey onu kullanmadan önce) ve yapılandırmanın her Uygula / Tamam işleminden
  sonra çalışır. Başlangıçta oluşturulan nesneler (bir dosya sisteminin aracı ...) gerektiğinde güncel değeri okumalıdır,
  oluşturuldukları değeri tutmamalıdır. Yalnızca yeniden başlatınca geçerli olan bir ayar bunu
  `Description` içinde söyler.
- **Metinler** (etiketler, açıklamalar, seçenek metinleri, `Validate` iletileri) İngilizcedir; çevirileri
  eklentinin diğer metinleri gibi eklentinin `languages` klasörüne gider. Yalnızca bir boşluk ve bir sözcükten oluşanlar ("{0} rows") yerine
  sabit bir başlangıcı olan kalıpları tercih edin ("The program was not
  found: {0}"): bir kalıp programın herhangi bir yerinde bütün metinlerle eşleşir.
- **Gizli değerler** (parola, belirteç, anahtar): `PluginSettingKind.Secret` kullanın. Gizli yazılır ve
  programın parola deposunda (varsa ana parola ile) tutulur, hiçbir zaman ayar dosyasında değil; alan
  bir değerin kayıtlı olup olmadığını gösterir ve bir **Unut** düğmesi vardır. Gerektiğinde `await values.GetSecretAsync(key)` ile isteyin
  (bağlanırken ...), `Apply` içinde değil: kilitli bir depo ana parolayı sorar. `Apply` işlevinin aldığı değerler
  nesnesini saklayın. Diğer türler düz metin olarak saklanır. Depolar eklentisinin erişim belirteçleri bir
  örnektir (`RepositoriesSettings`: belirteç ilk istekte bir kez sorulur ve sonraki `Apply` işlemine kadar tutulur).

## Dosya listesi menüsündeki eylemler

Bir dosya sistemi eklentisi kendi girişleri için eylemler sunabilir (bir konteyner başlat, bir uzak bağlantıyı yenile ...): imleç böyle bir girişteyken
dosya listesinin sağ tık menüsünün en üstündedirler.

```csharp
public IReadOnlyList<PluginAction> ActionsFor(VfsEntry entry) =>
    IsContainer(entry)
        ? [new("Start the container", PluginActionIcon.Start, async ct => { await StartAsync(entry, ct); return null; })]
        : [];
```

`ActionsFor` menü açıldığında çağrılır: hafif tutun (ağ yok). `RunAsync` gösterilecek bir ileti ya da
null döndürür; bir `IOException` ileti olarak gösterilir; klasör sonrasında yeniden okunur. Simge `PluginActionIcon` değerlerinden biridir
(Start, Stop, Restart, Pause, Delete, Refresh, Info, Other) ve simge paketi üzerinden çizilir. Ad
İngilizcedir; çevirisi eklentinin languages klasörüne gider.

## Çeviriler

Bir eklenti, kendi çevirilerini derlemesinin yanındaki bir `languages` klasöründe, programın dil dosyalarıyla aynı biçimde getirir
([Diller](languages.md)): `en.json` her anahtarı eklentinin her İngilizce metnine eşler ve diğer her
dil dosyası aynı anahtarları çevirilerine eşler.

```text
plugins/hello/
  Hello.Plugin.dll
  plugin.json
  languages/
    en.json     { "Hello.Places": "Hello places", "Hello.NotFound": "Not found: {0}" }
    tr.json     { "Hello.Places": "Hello yerleri", "Hello.NotFound": "Bulunamadı: {0}" }
```

- Kodda metinleri İngilizce yazın, `en.json` içindekiler gibi; kodda başka bir şey gerekmez. Programın
  gösterdiği şeyler (menü girişleri, sütun adları, istisna iletileri) bu dosyalar aracılığıyla çevrilir. Eklentinin kendisinin çevirdiği bir metin, örneğin bir sütunun değeri,
  `NextCommander.Core.Localization.Localizer.T("Text")` kullanır.
- `{0}`, `{1}` değişen parçaları (bir ad, bir sayı) temsil eder; her çeviri bunları korur.
- Bir dil yüklendiğinde, açık olan eklentilerin dosyaları programınkilere eklenir. Programın aynı
  İngilizce metin için kendi çevirisi varsa o kullanılır; bu yüzden eklentinin başka bir anlama gelen bir metninin
  kendine ait İngilizcesi olmalıdır (Git eklentisi *Changed* der, çünkü *Modified* programın tarih sütunudur).
- Bir dil için dosyası olmayan eklenti, metinlerini o dilde İngilizce gösterir.

## Test ve sorun bulma

- **Birim testleri:** bir test projesinde sınıflarınızı doğrudan oluşturun ve çağırın; dosya sistemi arayüzünün çevresinde
  program gerekmez. Programın kendi testleri de aynısını yapar (`tests/NextCommander.Core.Tests/RcloneTests.cs`
  rclone eklentisini sahte bir API'ye karşı çalıştırır).
- **Yükleme testi:** `PluginLoader.LoadAll([folder])`, bir eklenti klasörünü programın yaptığı gibi yükler ve
  bir şey yanlışsa `LoadedPlugin.Error` değerini doldurur (`RcloneTests.Loads_as_a_plugin_with_its_entry_point`).
- **Programda:** eklentiyi derleyin, çıktıyı ve `plugin.json` dosyasını `<settings folder>/plugins/<name>` içine kopyalayın,
  NextCommander'ı başlatın, sonra **Eklentiler...** penceresini ve günlüğü kontrol edin. Dosyaları kopyalayan bir derleme sonrası adımı
  zaman kazandırır (Windows ayarlar klasörü; Linux'ta `$(HOME)/.config/NextCommander/plugins/hello`):

  ```xml
  <Target Name="CopyToNextCommander" AfterTargets="Build">
    <ItemGroup>
      <PluginFiles Include="$(OutDir)**\*" />
      <PluginFiles Include="plugin.json" />
    </ItemGroup>
    <Copy SourceFiles="@(PluginFiles)" DestinationFolder="$(APPDATA)\NextCommander\plugins\hello\%(RecursiveDir)" />
  </Target>
  ```

- **Hata ayıklama:** NextCommander'ı IDE'nizden başlatın (ya da hata ayıklayıcıyı çalışan `NextCommander` işlemine bağlayın);
  eklentideki kesme noktaları her zamanki gibi çalışır.
- **Eklentiyi güncelleme:** program çalışırken eklentinin derlemesini açık tutar. NextCommander'ı kapatın, dosyaları
  değiştirin, yeniden başlatın.

## Eklentiyi NextCommander ile birlikte sunma

Programla birlikte gelen eklentiler `src/Plugins/<Project>` içinde yer alır ve onunla birlikte derlenir:

1. Projeyi yukarıdaki gibi, `src/Plugins/NextCommander.Plugin.<Name>` içinde, `plugin.json` dosyası proje
   klasöründe olacak şekilde oluşturun. `NextCommander.slnx` dosyasına ekleyin.
2. `src/NextCommander.App/NextCommander.App.csproj` içinde, rclone eklentisi gibi ekleyin:

   ```xml
   <ProjectReference Include="..\Plugins\NextCommander.Plugin.Hello\NextCommander.Plugin.Hello.csproj"
                     ReferenceOutputAssembly="false" OutputItemType="NcShippedPlugin" PluginFolder="hello" />
   ```

   Uygulama derlemeye başvurmaz. Derleme, onu proje klasörünün `plugin.json` dosyasıyla birlikte programın yanındaki
   `plugins/hello` klasörüne kopyalar; derleme çıktısında ve yayım klasöründe (tek dosyanın dışında),
   böylece zip, kurulum ve Linux ile macOS paketleri onu içerir.
3. Kullanıcının gördüğü metinler (`EntryName`, `Unavailable`, hata iletileri) İngilizce yazılır; çevirilerini
   eklentinin kendi `languages` klasörüne koyun ([Çeviriler](#translations) bölümüne bakın), programın sunduğu her
   dil için bir dosyayla (en, tr, de, es, fr, zh). Derleme klasörü `plugins/hello/languages` içine kopyalar.
   Kendi paketleri olan bir eklenti (DiscUtils içeren disk imajı eklentisi gibi) `CopyLocalLockFileAssemblies` ayarlar,
   Core'a `ExcludeAssets="runtime;native"` ile başvurur (programın paketleri paylaşılır, kopyalanmaz) ve
   `ReferenceCopyLocalPaths` değerini döndüren bir `NcPluginDependencies` hedefine sahiptir: derleme bu derlemeleri ve eklentinin `.deps.json` dosyasını
   yükleme bağlamının bulduğu klasörüne koyar.
4. `tests/NextCommander.Core.Tests` içine testler ekleyin (eklenti projesine oradan başvurun).
