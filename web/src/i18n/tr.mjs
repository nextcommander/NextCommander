// Sitenin Türkçe metinleri. en.mjs ile aynı anahtarlar, aynı yapı (build.mjs denetler).
// Dosya, komut ve program adları çevrilmez; kısayollar programdaki gibidir.

export default {
  lang: "tr",
  locale: "tr_TR",
  name: "Türkçe",

  meta: {
    "": { title: "NextCommander — klavyeyle yönetilen çift panelli dosya yöneticisi", description: "Windows, Linux ve macOS için çift panelli dosya yöneticisi: iki panel, sekmeler, arşivler, sunucular, bulutlar, konteynerler, karşılaştırma ve birleştirme; hepsi birkaç tuş uzağınızda." },
    features: { title: "Özellikler — NextCommander", description: "NextCommander'ın yaptığı her şey: paneller ve sekmeler, geri alınabilen dosya işlemleri, görüntüleyici ve düzenleyici, arama, karşılaştırma ve birleştirme, arşivler, otomasyon ve görünüm." },
    connections: { title: "Sunucular, bulutlar ve cihazlar — NextCommander", description: "FTP, SFTP, SMB, WebDAV, S3, Azure, Google Drive, OneDrive, Dropbox, Box, Docker, Kubernetes, telefonlar, RDP, SSH ve dahası, yerel dosyalarınızla aynı panellerde." },
    plugins: { title: "Eklentiler — NextCommander", description: "Git, Docker, Kubernetes, SQLite, rclone, GitHub ve GitLab, sanal diskler, paketler ve görüntüleyiciler eklenti olarak gelir. Kendi eklentinizi C# ile yazın." },
    download: { title: "İndir — NextCommander", description: "NextCommander'ı Windows, Linux veya macOS için indirin; x64 ve Arm. Kendi içinde eksiksiz paketler, taşınabilir zip; başka hiçbir şey kurmanız gerekmez." },
    guide: { title: "Rehber — NextCommander", description: "NextCommander rehberi: başlarken, klavye kısayolları, araçlar, arşivler, bağlantılar, yapılandırma, temalar, betikler ve eklentiler." },
    faq: { title: "Sorular ve yanıtlar — NextCommander", description: "NextCommander'ın kurulumu, ayarları, bağlantıları, güvenliği, dilleri ve eklentileri hakkında yanıtlar." },
  },

  nav: {
    "": "Ana sayfa", features: "Özellikler", connections: "Bağlantılar", plugins: "Eklentiler", guide: "Rehber", download: "İndir", faq: "SSS",
    skip: "İçeriğe geç", menu: "Menü", theme: "Açık veya koyu", language: "Dil", github: "GitHub",
  },

  common: {
    download: "İndir",
    downloadFor: "{0} için indir",
    getStarted: "Başlangıç rehberi",
    allFeatures: "Tüm özellikler",
    seeConnections: "Tüm bağlantı türleri",
    seePlugins: "Eklentiler",
    readGuide: "Rehberi okuyun",
    latestRelease: "GitHub'daki son sürüm",
    copy: "Kopyala",
    copied: "Kopyalandı",
    shortcut: "Kısayol",
    where: "Yeri",
    screenshotOf: "Ekran görüntüsü: {0}",
    onThisPage: "Bu sayfada",
    needs: "Gerekenler",
  },

  home: {
    pill: "Windows · Linux · macOS · x64 ve Arm",
    titleA: "İki panel.",
    titleB: "Her dosya birkaç tuş uzağınızda.",
    lead: "NextCommander, klavyeden yönettiğiniz çift panelli bir dosya yöneticisidir. Paneller arasında tek tuşla kopyalayın; arşivleri, sunucuları, bulutları ve konteynerleri klasör gibi açın; karşılaştırın, birleştirin ve ellerinizi klavyeden kaldırmadan çalışın.",
    fkeys: [["F3", "Görüntüle"], ["F4", "Düzenle"], ["F5", "Kopyala"], ["F6", "Taşı"], ["F7", "Yeni klasör"], ["F8", "Sil"]],
    stats: [["25+", "bağlantı türü"], ["9", "hazır eklenti"], ["6", "yerleşik dil"], ["4", "aynı anda panel"]],
    why: {
      eyebrow: "Neden NextCommander",
      title: "Çok dosya taşıyanlar için yapıldı",
      lead: "Diskinde, arşivlerde, sunucularda ve bulutta — fareye uzanmadan.",
      items: [
        { icon: "keyboard", title: "Önce klavye", body: "Her eylem, tuşunu değiştirebileceğiniz bir komuttur. Komut paleti, birkaç harf yazınca hepsini bulur." },
        { icon: "globe", title: "Her dosya tek yerde", body: "Yerel diskler, arşivler, FTP ve SFTP, S3, bulut sürücüler, Docker, Kubernetes ve telefonlar aynı panellerde açılır ve birbirine kopyalanır." },
        { icon: "undo", title: "Hızlı çalışmak güvenli", body: "İşler arka planda kuyruk ve duraklatmayla yürür; Ctrl+Z kopyalamayı, taşımayı, yeniden adlandırmayı ve Geri Dönüşüm Kutusu'na silmeyi geri alır." },
        { icon: "layers", title: "Her şey kutunun içinde", body: "Görüntüleyici, düzenleyici, karşılaştırma ve birleştirme, çoklu yeniden adlandırma, eşitleme, klasör boyutları ve terminal — kendi içinde eksiksiz paketler, başka kurulum yok." },
      ],
    },
    tour: {
      eyebrow: "Tur",
      title: "Programdaki gibi gezinin",
      lead: "İmleci <kbd>↑</kbd> <kbd>↓</kbd> ile taşıyın ya da bir satıra tıklayın; sağ taraf, Hızlı Görünüm gibi onu gösterir.",
      listTitle: "Ad",
      keyTitle: "Tuş",
      items: [
        { name: "İki panel ve sekmeler", key: "Tab", shot: "main-light.png", body: "Yan yana iki dosya listesi, her birinde klasör sekmeleri. Aralarında tek tuşla kopyalayın, taşıyın, karşılaştırın; bir tarafı ikiye bölün, dört panele kadar." },
        { name: "Hızlı görünüm", key: "Ctrl+Q", shot: "quick-view-right.png", body: "İmlecin altındaki dosya listenin yanında görünür: resimler, belgeler, PDF sayfaları, sözdizimi renkli kod; siz ilerledikçe." },
        { name: "Komut paleti", key: "Ctrl+Shift+P", shot: "command-palette.png", body: "Her komut, klasör, Hotlist girdisi ya da kayıtlı bağlantı, birkaç harf yazarak bulunur." },
        { name: "Görüntüleyici", key: "F3", shot: "lister.png", body: "Sözdizimi renkli metin, hex, kamera RAW dahil resimler, ses ve video, Markdown, Office olmadan Office belgeleri." },
        { name: "Resim bilgileri", key: "F3, 6", shot: "picture-metadata.png", body: "Kamera, objektif, pozlama, harita bağlantılı GPS konumu, IPTC ve XMP; bir fotoğrafın tüm etiketleri." },
        { name: "Arşivler", key: "Enter", shot: "archive.png", body: "zip, 7z, rar, tar, iso, sanal makine diskleri, .deb, .rpm ve .msi paketleri klasör gibi açılır." },
        { name: "Dosya bul", key: "Alt+F7", shot: "find-files.png", body: "Ada, içeriğe, boyuta, tarihe ve özniteliklere göre; Office dosyalarının içinde de. Kayıtlı bir arama, sonuçlarından oluşan bir klasör gibi açılır." },
        { name: "Çoklu yeniden adlandırma", key: "Ctrl+M", shot: "multi-rename.png", body: "Kalıplar, sayaçlar, bul ve değiştir ile birçok dosyayı birden adlandırın; her yeni ad önceden görünür." },
        { name: "Karşılaştır ve birleştir", where: "Dosya menüsü", shot: "compare.png", body: "İki dosya yan yana, yerinde düzenlenir; farkları karşıya kopyalayın, üç dosyayı birleştirin, yama oluşturup uygulayın." },
        { name: "Klasörleri eşitle", where: "Araçlar menüsü", shot: "synchronize.png", body: "İki klasörü tarihe, boyuta ya da içeriğe göre karşılaştırın; tek yönde ya da iki yönde eşitleyin." },
        { name: "Klasör boyutları", where: "Araçlar menüsü", shot: "disk-usage.png", body: "Yer nereye gitti: sıralanabilen ağaç, ağaç haritası ve dosya türleri; silme hemen orada." },
        { name: "Bağlantı yöneticisi", key: "Ctrl+F", shot: "connections.png", body: "Kayıtlı sunucular, bulutlar ve oturumlar klasörlerde; her türün kendi seçenekleri ve her bağlantının kendi günlüğü." },
      ],
    },
    connections: {
      eyebrow: "Bağlantılar",
      title: "Sunucular, bulutlar ve cihazlar aynı panellerde",
      lead: "Uzaktaki bir klasör de bir klasördür: <kbd>F5</kbd> ile kopyalayın, <kbd>F3</kbd> ile görüntüleyin, yerel bir klasörle karşılaştırın.",
      chips: ["FTP / FTPS", "SFTP", "SCP", "WebDAV", "SMB", "Amazon S3", "Azure Blob", "Azure Files", "Google Drive", "OneDrive", "Dropbox", "Box", "rclone", "Docker", "Kubernetes", "GitHub", "GitLab", "Android (ADB)", "MTP telefonlar", "PC bağlantısı", "RDP", "VNC", "SSH", "Telnet", "Seri port"],
    },
    looks: {
      eyebrow: "Görünüm",
      title: "Çalıştığı sisteme uyar",
      lead: "Sistemin açık ya da koyu modunu izler veya macOS ya da Ubuntu görünümüne bürünür. Üç simge paketi, altı yerleşik dil, kendi temalarınız; her kısayol ve düğme değiştirilebilir.",
      options: [["Açık", "main-light.png"], ["Koyu", "main-dark.png"], ["Ubuntu", "main-ubuntu.png"], ["macOS koyu", "main-macos-dark.png"]],
    },
    cta: {
      title: "Kendi dosyalarınızda deneyin",
      lead: "Windows, Linux ve macOS için, x64 ve Arm, kendi içinde eksiksiz paketler. Taşınabilir zip bir USB bellekten çalışır.",
    },
  },

  features: {
    eyebrow: "Özellikler",
    title: "Bir dosya yöneticisinin yapması gereken her şey, klavyeden",
    lead: "NextCommander'ın yaptıklarına bölüm bölüm bir bakış. Her bölüm, ayrıntılar için kendi rehberine bağlanır.",
    sections: [
      {
        id: "panels", icon: "columns", title: "İki panel ve sekmeler", shot: "tree-and-thumbnails.png", guide: "getting-started",
        lead: "Yan yana iki dosya listesi; aralarında tek tuşla kopyalayın, taşıyın ve karşılaştırın.",
        points: [
          "Her panelde klasör sekmeleri: açın, kapatın, sürükleyerek sıralayın, kilitleyin, adlandırın; sekme takımlarını kaydedip yükleyin.",
          "Kısa, tam, açıklamalı ve küçük resimli görünümler; panelin içinde klasör ağacı; fotoğraf boyutları, müzik etiketleri ya da satır sayıları gibi kendi sütun takımlarınız.",
          "Klasör geçmişi, en sık kullanılan klasörler ve sevdiğiniz klasörlerin Hotlist'i.",
          "Yazarken hızlı arama ve hızlı süzgeç; Dal Görünümü tüm alt klasörlerin dosyalarını tek listede gösterir.",
          "Bir tarafı yan yana ya da alt alta iki panele bölün — dört panele kadar.",
          "Kayıtlı çalışma alanları: iki panelin sekmeleri ve terminal bir ad altında, tek tuşla yeniden açılır.",
        ],
      },
      {
        id: "operations", icon: "copy", title: "Dosya işlemleri", shot: "permissions.png", guide: "getting-started",
        lead: "F tuşlarıyla, sürükle-bırakla ya da pano üzerinden kopyalayın, taşıyın, adlandırın, silin.",
        points: [
          "Kopyalama ve taşıma arka planda yürür: ilerleme penceresi, kuyruk, duraklatma ve hız sınırı; kopyalar yazıldıktan sonra doğrulanabilir.",
          "<kbd>Ctrl+Z</kbd> kopyalamayı, taşımayı, yeniden adlandırmayı, çoklu yeniden adlandırmayı, yeni klasörü ve Geri Dönüşüm Kutusu'na silmeyi geri alır.",
          "Yetki eksikliğinden başarısız olan bir işlem, sistemin kendi penceresiyle yönetici olarak yeniden çalıştırmayı önerir.",
          "Dosya ve klasörlere renkli etiketler, Etiketler sütunu ve etikete göre süzgeç.",
          "Program açıkken işleyen kurallar: gelen dosyalar taşınır ya da kopyalanır, klasörler her gece eşitlenir.",
          "İzinler: Unix modu, sahip ve grup (SFTP üzerinden de) ve Windows erişim listeleri tek pencerede. Güvenli silme dosyaların üzerine önce rastgele veri yazar.",
        ],
      },
      {
        id: "viewer", icon: "eye", title: "Görüntüleyici ve düzenleyici", shot: "lister.png", guide: "tools#lister-f3",
        lead: "Lister (F3) ve Hızlı Görünüm başka bir program olmadan neredeyse her dosyayı gösterir.",
        points: [
          "Sözdizimi renkli metin, ikili ve hex, kamera RAW dahil resimler, ses ve video (VLC kuruluysa).",
          "Markdown, HTML ve RTF önizlemeleri; PDF sayfaları; Office olmadan Word, Excel, PowerPoint ve OpenDocument dosyaları.",
          "Sıralanabilen tablo olarak CSV; ağaç olarak JSON, XML ve YAML; e-postalar (.eml, .msg), e-kitaplar, Jupyter defterleri, sertifikalar, programlar, yazı tipleri, kişiler ve takvimler.",
          "Resim bilgileri: kamera, objektif, pozlama, harita bağlantılı GPS, IPTC, XMP ve tüm etiketler.",
          "Dosyanın kodlamasını ve satır sonlarını koruyan yerleşik metin düzenleyici (F4).",
        ],
      },
      {
        id: "search", icon: "search", title: "Arama ve araçlar", shot: "find-files.png", guide: "tools#find-files-altf7",
        lead: "Aradığınızı bulun, birçok dosyayı birden değiştirin.",
        points: [
          "Dosyaları ada, içeriğe, boyuta, tarihe ve özniteliklere göre bulun — Office ve OpenDocument dosyalarının içinde de.",
          "Kayıtlı bir arama, sonuçlarından oluşan bir klasör gibi panelde açılır ve her yenilemede yeniden aranır.",
          "İsteğe bağlı dosya dizini aramaları ve klasör boyutlarını neredeyse anında getirir; Everything, plocate ve Spotlight varsa kullanılır.",
          "Kalıplar, sayaçlar, bul ve değiştir ve her adın önizlemesiyle çoklu yeniden adlandırma.",
          "Yinelenen dosyalar, ağaç haritalı klasör boyutları, sağlama toplamları (BLAKE3 dahil), bölme ve birleştirme, yerleşik terminal.",
        ],
      },
      {
        id: "compare", icon: "diff", title: "Karşılaştırma ve birleştirme", shot: "compare.png", guide: "tools#compare-by-content",
        lead: "Farklar yan yana, yerinde düzenlenir — ayrı bir fark aracı gibi, ama içinde.",
        points: [
          "İki dosya yan yana, farklar kelime düzeyine kadar; blokları karşıya kopyalayın, geri alın, kaydedin.",
          "Büyük/küçük harfi, boşlukları, boş satırları ve satır sonlarını yok sayın; düzenli ifadelerle satır süzgeçleri; taşınan bloklar işaretlenir.",
          "Sonuç bölmeli üç yollu birleştirme, çakışmalar arasında gezinme ve \"soldakini / sağdakini / ikisini al\"; Git çakışmalarıyla çalışır.",
          "Hex karşılaştırma, fark katmanlı resim karşılaştırma, metin olarak karşılaştırılan PDF ve Office belgeleri.",
          "Klasörleri eşitle, klasörleri tarihe, boyuta ya da içeriğe göre karşılaştırır, her çifti açar ve eşitler.",
          "Unified yama oluşturun, önizleyerek uygulayın, HTML rapor olarak dışa aktarın.",
        ],
      },
      {
        id: "archives", icon: "archive", title: "Arşivler, diskler ve paketler", shot: "archive.png", guide: "archives",
        lead: "Kapsayıcılar klasör gibi açılır — Enter içeri girer, Backspace geri çıkar.",
        points: [
          "zip, 7z, rar, tar ve diğer arşivler, .iso kalıpları; zip dosyaları yerinde değiştirilir.",
          "zip, tar, iso ve (7-Zip kuruluysa) 7z olarak, parolayla paketleyin; arşivleri açın ve sınayın; .iso kalıplarını (Windows'ta .vhd / .vhdx de) bağlayın.",
          "Sanal makine diskleri (.vhd, .vhdx, .vmdk, .vdi, .dmg) bölümlerinin klasörleri olarak: NTFS, FAT, ext, XFS, Btrfs, HFS+ ve LVM, salt okunur, her sistemde.",
          "Paketler ve kurulum dosyaları: .deb, .rpm, .cab, .msi (dosyalar kurulacakları yollarda) ve konteyner kalıpları (docker save, OCI).",
          "SQLite veritabanları tablolarının klasörleri olarak; bir Git deposunun dalları, etiketleri ve commit'leri klasör olarak.",
        ],
      },
      {
        id: "automation", icon: "bolt", title: "Makrolar ve betikler", shot: "command-palette.png", guide: "scripts",
        lead: "Tekrarladığınız işi ona öğretin.",
        points: [
          "Çalıştırdığınız komutları makro olarak kaydedin — tuşu ve düğmesi olan kendi komutunuz.",
          "Programı yöneten JavaScript komutları, JavaScript ile yazılmış özel sütunlar ve bir web hizmetinin dosyalarını panelde gösteren dosya sistemleri.",
          "Her komut, klasör, Hotlist girdisi ve bağlantı için komut paleti (<kbd>Ctrl+Shift+P</kbd>).",
          "Her kısayol, menü girdisi ve düğme çubuğu değiştirilebilir.",
        ],
      },
      {
        id: "looks", icon: "palette", title: "Görünüm ve diller", shot: "configuration-color.png", guide: "themes",
        lead: "Bir ayar dosyası düzenlemeden kendinize göre yapın.",
        points: [
          "Sistemin açık / koyu modunu izler; Koyu, macOS ve Ubuntu görünümleri yerleşik ya da tema düzenleyicisinden kendi renk temanız.",
          "Üç simge paketi (Renkli, Tek renk, Fluent) ya da kendi paketiniz.",
          "İngilizce, Türkçe, Almanca, İspanyolca, Fransızca ve Çince yerleşik; tek bir dosyayla dil ekleyin.",
          "Her açılışta ayar yedeği; parça parça geri yüklenir: ayarlar, bağlantılar, Hotlist, kısayollar, düğme çubukları, tema.",
        ],
      },
    ],
  },

  connections: {
    eyebrow: "Bağlantılar",
    title: "Sunucular, bulutlar ve cihazlar aynı panellerde",
    lead: "Her bağlantı bir klasör olarak açılır. Kopyalama, taşıma, görüntüleme, düzenleme, karşılaştırma ve eşitleme hepsinde çalışır — bir SFTP sunucusundan bir OneDrive klasörüne, bir Docker biriminden bir telefona.",
    shot: "connections.png",
    groups: [
      { icon: "server", title: "Dosya sunucuları", items: [["FTP ve FTPS", "Açık ve örtük TLS, etkin ve edilgen kip, sabitlenmiş sertifikalar, bağlantı başına gizli dosyalar."], ["SFTP ve SCP", "Anahtarlar, SSH ajanı ve Pageant, atlama sunucuları, sabitlenmiş sunucu anahtarları, bağlantıyı canlı tutma."], ["WebDAV", "Bilinen hizmetler için hazır ayarlar."], ["SMB", "Windows paylaşımları ve Samba; Linux ve macOS'tan da."], ["Web klasör listeleri", "http:// ve https:// dizin sayfaları klasör gibi, salt okunur açılır."]] },
      { icon: "bucket", title: "Nesne depolama", items: [["Amazon S3 ve uyumlu depolar", "AWS profilleri ve SSO, depolama sınıfları, S3 uyumlu hizmetler için hazır ayarlar."], ["Azure Blob Storage ve Azure Files", "Paylaşılan anahtar ya da Microsoft Entra ile oturum açma."]] },
      { icon: "cloud", title: "Bulut sürücüler", items: [["Google Drive", "Benimle paylaşılanlar ve paylaşılan sürücüler dahil."], ["OneDrive", "SharePoint kitaplıkları dahil."], ["Dropbox ve Box", "Tarayıcı üzerinden oturum açılır."], ["rclone", "Kurulu bir rclone'un tüm uzak depoları."], ["Eşitleme klasörleri", "OneDrive, iCloud Drive, Dropbox, Google Drive ve WSL'nin yerel klasörlerine kısayollar."]] },
      { icon: "box", title: "Konteynerler ve kod", items: [["Docker", "Konteynerler ve birimler; durmuş konteynerler salt okunur; menüden başlatın, durdurun, yeniden başlatın."], ["Kubernetes", "Bağlam, ad alanı, pod ve konteyner klasör olarak; günlükler, describe, silme, yeniden başlatma."], ["GitHub ve GitLab", "Klonlamadan dallar, etiketler, sürümler ve dosyalar; tek komutla klonlama."]] },
      { icon: "phone", title: "Cihazlar", items: [["ADB ile Android telefonlar", "USB hata ayıklama; telefonlar sürücü olarak görünür."], ["MTP ile telefonlar ve kameralar", "Telefondan fotoğrafları alın, dosya koyun; Windows, Linux ve macOS'ta."], ["PC bağlantısı", "NextCommander çalıştıran iki bilgisayar klasörlerini USB ya da Ethernet kablosuyla veya ağ üzerinden paylaşır; şifreli, bir kez kodla eşleştirilir."], ["Çöp kutusu", "Geri Dönüşüm Kutusu bir panelde: geri yükleyin, dışarı kopyalayın, kalıcı olarak silin."]] },
      { icon: "terminal", title: "Oturumlar", items: [["Uzak masaüstü (RDP)", "Windows ve Linux'ta ana pencerenin bir sekmesinde ya da sistemin istemcisinde."], ["VNC", "Sistemin görüntüleyicisinde."], ["SSH, Telnet, rsh", "Yerleşik terminalin sekmelerinde."], ["Seri portlar", "COM ve tty portlarındaki cihazlar için konsol."]] },
    ],
    extras: {
      title: "Her bağlantının çevresinde",
      items: [
        { icon: "folder", title: "Bağlantı yöneticisi", body: "Kayıtlı bağlantılar sürükle-bırakla klasörlerde; yeniler için sihirbaz, sınama düğmesi, FileZilla ve WinSCP'den içe aktarma ve yerel ağınızdaki makineler." },
        { icon: "lock", title: "Kayıtlı parolalar", body: "Şifreli saklanır, isteğe bağlı bir ana parolayla korunur; sunucu anahtarları ve sertifikalar sabitlenir, değiştiklerinde sorulur." },
        { icon: "route", title: "Vekil sunucular ve tüneller", body: "Sistemin vekil sunucusu, HTTP, HTTPS, SOCKS4 / 4a / 5, bir SSH tüneli ya da PAC betiği — tüm program için ya da bağlantı başına. SSH üzerinden yerel, uzak ve SOCKS port yönlendirme." },
        { icon: "log", title: "Bağlantı başına günlük", body: "Her bağlantı olanları yazar — bağlanma, oturum açma, anahtarlar, nedenleriyle hatalar — ama asla parola yazmaz. \"Günlüğü göster\" bir hatadan tek tıklama uzakta." },
      ],
    },
  },

  plugins: {
    eyebrow: "Eklentiler",
    title: "Eklenti olarak gelir, sizinkilere açıktır",
    lead: "NextCommander ile dokuz eklenti gelir ve açılışta yüklenir. Her biri kapatılabilir, ayarları yapılandırmanın Eklentiler sayfasındadır ve kendi çevirilerini getirir.",
    items: [
      { name: "Git", icon: "git", needs: "git", body: "Bir deponun dalları, etiketleri ve commit'leri salt okunur klasörler olarak; her dosyanın durumunu ve son commit'ini gösteren Git sütunları; menüden stage, commit, dal değiştirme, fetch, pull, push ve merge." },
      { name: "Docker", icon: "box", needs: "docker", body: "Konteynerlerin ve birimlerin dosyaları bir panelde; durmuş konteynerler salt okunur; sağ tık menüsünden başlatma, durdurma, yeniden başlatma, duraklatma ve sürdürme." },
      { name: "Kubernetes", icon: "wheel", needs: "kubectl", body: "Bağlamlar, ad alanları, pod'lar ve konteynerler klasör olarak; pod günlükleri, describe, silme ve deployment'ı yeniden başlatma; durum, hazır, yeniden başlama ve düğüm sütunları." },
      { name: "SQLite", icon: "db", needs: "", body: "Veritabanları tablolarının klasörleri olarak açılır; bir tabloyu CSV, JSON ya da şeması olarak dışarı kopyalayın; F3 tabloları, sütunları, ilk satırları ve dizinleri gösterir." },
      { name: "rclone", icon: "cloud", needs: "rclone", body: "Kurulu bir rclone'un tüm uzak depoları — onlarca bulut hizmeti — aynı panellerde." },
      { name: "GitHub ve GitLab", icon: "code", needs: "", body: "Depolar klonlamadan, web API'leri üzerinden salt okunur: dallar, etiketler, sürümler ve dosyalar; GitHub Enterprise ve kendi sunucunuzdaki GitLab da; belirteçler gizli olarak saklanır." },
      { name: "Sanal diskler", icon: "disk", needs: "", body: ".vhd, .vhdx, .vmdk, .vdi ve .dmg birimlerinin klasörleri olarak: NTFS, FAT, ext, XFS, Btrfs, HFS+ ve LVM, salt okunur, her sistemde." },
      { name: "Paketler", icon: "package", needs: "", body: ".deb, .rpm, .cab, .msi ve konteyner kalıpları klasör olarak; F3 adı, sürümü, bağımlılıkları ya da kalıbın yapılandırmasını gösterir." },
      { name: "Görüntüleyiciler", icon: "eye", needs: "", body: "Lister'da CSV tabloları, JSON / XML / YAML ağaçları, e-postalar, e-kitaplar, defterler, sertifikalar, programlar, yazı tipleri, kişiler ve resim bilgileri." },
    ],
    needsLabel: "Kurulu olanı kullanır:",
    builtIn: "Başka bir şey gerekmez",
    own: {
      title: "Kendinizinkini yazın",
      lead: "Bir eklenti, eklentiler klasöründeki bir .NET derlemesidir. Bunlardan herhangi birini ekleyebilir; ayarlarını program onun için çizer.",
      points: [
        ["Dosya sistemleri", "Panelde açılan kendi şemanız (hello://); kopyalama, görüntüleme ve düzenleme onunla da çalışır."],
        ["Sütunlar", "Her görünüm kipinde kendi alanlarınız."],
        ["Görüntüleyiciler", "Lister'ın eklentiniz aracılığıyla gösterdiği dosyalar."],
        ["Paketleyiciler", "Paketle penceresinde bir arşiv biçimi."],
        ["Eylemler", "Eklentiler menüsünde ve dosya listesinin menüsünde, sorular ve onaylarla girdiler."],
        ["Ayarlar", "Eklentiler sayfasında metin, sayı, seçim, program, klasör ve gizli değerler."],
      ],
    },
  },

  download: {
    eyebrow: "İndir",
    title: "Kendi içinde eksiksiz, başka kurulum yok",
    lead: "Her paket gerekenleri yanında taşır. Sisteminizi seçin; paketler son sürümde.",
    platforms: [
      {
        os: "windows", name: "Windows", archs: "Windows 10 ve 11 · x64 ve Arm64",
        files: [["-setup.exe", "Yönetici yetkisi olmadan kullanıcınız için ya da tüm kullanıcılar için kurar; Başlat menüsü, isteğe bağlı masaüstü simgesi ve Gezgin'de \"NextCommander'da aç\"."], [".zip", "İstediğiniz yere açın ve NextCommander.exe'yi başlatın."], ["-portable.zip", "Ayarlarını kendi klasöründe tutar; USB bellekten çalışır."]],
        command: "",
      },
      {
        os: "linux", name: "Linux", archs: "x64 ve Arm64",
        files: [[".deb", "Debian, Ubuntu, Mint ..."], [".rpm", "Fedora, RHEL, openSUSE ..."], [".tar.gz", "Açın ve NextCommander'ı başlatın."]],
        command: "sudo apt install ./NextCommander-<version>-linux-x64.deb",
      },
      {
        os: "mac", name: "macOS", archs: "Apple silicon ve Intel",
        files: [["NextCommander.app", "Bir zip ya da disk kalıbının içinde; Uygulamalar klasörüne sürükleyin."]],
        command: "",
        note: "Kayıtlı bir geliştirici tarafından imzalanmamış bir kopya önce Sistem Ayarları › Gizlilik ve Güvenlik › Yine de Aç gerektirebilir.",
      },
    ],
    get: "{0} için indir",
    yours: "Sizin sisteminiz",
    helpers: {
      title: "İsteğe bağlı yardımcılar",
      lead: "Bazı özellikler belki zaten kurulu olan programları kullanır. Onlar yoksa yalnızca o özellik kapanır.",
      items: [["7-Zip", "7z arşivleri oluşturmak için"], ["VLC", "Lister'da ses ve video oynatmak için"], ["Everything, plocate, Spotlight", "dizinli arama"], ["LibreOffice", "doc, xls, ppt, Visio ... önizlemeleri"], ["git, docker, kubectl, rclone", "aynı adlı eklentiler"]],
    },
    verify: {
      title: "İndirdiğinizi denetleyin",
      body: "Her sürümde bir SHA256SUMS dosyası vardır. İndirdiğiniz dosyanın sağlama toplamını oradaki satırıyla karşılaştırın:",
      windows: "Get-FileHash .\\NextCommander-*-setup.exe -Algorithm SHA256",
      unix: "sha256sum -c SHA256SUMS --ignore-missing",
    },
    after: {
      title: "Kurduktan sonra",
      items: [
        ["Ayarlarınız", "Tek bir klasörde durur (Windows: %APPDATA%\\NextCommander, Linux: ~/.config/NextCommander, macOS: ~/Library/Application Support/NextCommander) ve her açılışta yedeklenir."],
        ["Güncellemeler", "Yeni sürümü eskisinin üzerine kurun; ayarlarınız kalır. Program açılışta yeni sürümü denetleyebilir de (Yapılandırma › İşlem, varsayılan olarak kapalı)."],
        ["Kaldırma", "Windows: Ayarlar › Uygulamalar. Linux: paket yöneticiniz. macOS: uygulamayı Çöp Sepeti'ne taşıyın. Ayarlar klasörü siz silene kadar kalır."],
      ],
    },
  },

  faq: {
    eyebrow: "Sorular",
    title: "Sorular ve yanıtlar",
    lead: "Aradığınız burada yok mu? Rehberler her ayrıntıya iner.",
    groups: [
      {
        title: "Başlarken",
        items: [
          ["Çift panelli dosya yöneticisi nedir?", "Yan yana iki dosya listesi: üzerinde çalıştığınız ve hedef. Kopyalama (F5) ve taşıma (F6) hedef seçme pencereleri olmadan birinden ötekine gider; gerisini klavye yapar."],
          ["Başka bir şey kurmam gerekiyor mu?", "Hayır. Paketler .NET dahil kendi içinde eksiksizdir. 7-Zip, VLC, git ya da docker gibi programları yalnızca onlara ihtiyaç duyan özellikler kullanır."],
          ["Yönetici yetkisi gerekiyor mu?", "Hayır. Windows kurulumu varsayılan olarak kullanıcınız için kurar. Bir işlem daha fazla yetki isterse (bir sistem klasörü), NextCommander yalnızca o işlemi sistemin kendi penceresiyle çalıştırmayı sorar."],
          ["USB bellekte yanımda taşıyabilir miyim?", "Evet: taşınabilir zip ayarlarını programın yanında tutar. Yanında portable.json dosyası olan her kopya taşınabilir olur."],
        ],
      },
      {
        title: "Kullanırken",
        items: [
          ["Kendi kısayollarımı kullanabilir miyim?", "Her eylem bir komuttur ve her komuta istediğiniz tuşları verebilirsiniz. Menüler, düğme çubukları, kısayollar ve komut satırı aynı komutları çağırır."],
          ["Bir hatayı geri alabilir miyim?", "Ctrl+Z kopyalamayı, taşımayı, yeniden adlandırmayı, çoklu yeniden adlandırmayı, yeni klasörü ve Geri Dönüşüm Kutusu'na silmeyi geri alır."],
          ["Büyük klasörlerle ve yavaş sunucularla çalışır mı?", "İşlemler arka planda kuyruk, duraklatma ve hız sınırıyla yürür; siz çalışmaya devam edersiniz. İsteğe bağlı dosya dizini aramayı ve klasör boyutlarını neredeyse anında getirir."],
          ["Hangi diller yerleşik?", "İngilizce, Türkçe, Almanca, İspanyolca, Fransızca ve Çince (Basitleştirilmiş). Yeni bir dil, ayarlar klasöründe tek bir dosyadır; eklentiler kendi çevirilerini getirir."],
        ],
      },
      {
        title: "Bağlantılar ve güvenlik",
        items: [
          ["Parolalarım nerede saklanıyor?", "Ayarlar klasöründe şifreli olarak, isteğe bağlı bir ana parolayla korunur. Günlükler hiçbir zaman gizli bilgi içermez."],
          ["Doğru sunucuyla konuştuğumu nasıl bilirim?", "Sunucu anahtarları ve sertifikalar ilk bağlantıda sabitlenir; biri değişirse NextCommander bunu gösterir ve sorar."],
          ["Vekil sunucu üzerinden çalışabilir mi?", "Evet: sistemin vekil sunucusu, HTTP, HTTPS, SOCKS4 / 4a / 5, bir SSH tüneli ya da PAC betiği; tüm program ya da tek bir bağlantı için."],
          ["Benimle ilgili bir şey gönderiyor mu?", "Hayır. İnternete yalnızca açtığınız bağlantılar için ve açarsanız yeni sürümü denetlemek için çıkar."],
        ],
      },
      {
        title: "Ayarlar ve eklentiler",
        items: [
          ["Ayarlarım bozulursa ne olur?", "Son yedi ayar dosyası yedek olarak saklanır. Bozuk dosya kenara alınır, yüklenebilen en yeni yedek yerine geçer; herhangi bir yedeği parça parça da geri yükleyebilirsiniz."],
          ["Ayarlarımı başka bir bilgisayara taşıyabilir miyim?", "Yapılandırma › Dışa aktar onları bir dosyaya yazar, İçe aktar diğer makinede okur."],
          ["Nasıl eklenti yazarım?", "Eklenti, ihtiyacınız olan arabirimleri uygulayan bir .NET derlemesidir: dosya sistemleri, sütunlar, görüntüleyiciler, paketleyiciler, eylemler ve ayarlar. Eklenti rehberinde küçük bir örnek var."],
        ],
      },
    ],
  },

  guide: {
    eyebrow: "Rehber",
    title: "NextCommander rehberi",
    lead: "İlk açılıştan eklenti yazmaya kadar NextCommander'ı kullanmakla ilgili her şey.",
    onThisPage: "Bu sayfada",
    allGuides: "Tüm rehberler",
    previous: "Önceki",
    next: "Sonraki",
    untranslated: "Bu rehber henüz çevrilmedi; İngilizce gösteriliyor.",
    filter: "Rehberlerde ara",
    noMatch: "Eşleşen rehber yok.",
    groups: [["Başlangıç", ["getting-started", "keyboard-shortcuts"]], ["Çalışma", ["tools", "archives", "connections"]], ["Kendinize göre", ["configuration", "themes", "icon-packs", "languages"]], ["Daha ileri", ["scripts", "plugins", "settings-folder"]]],
    items: {
      "getting-started": ["Başlarken", "Pencere, gezinme, seçme, temel işlemler, sağ tık, sürükle-bırak, yönetici yetkileri."],
      "keyboard-shortcuts": ["Klavye kısayolları", "Panellerin, komut satırının ve araçların varsayılan kısayolları ve nasıl değiştirileceği."],
      tools: ["Araçlar", "Lister, düzenleyici, dosya bulma, çoklu yeniden adlandırma, eşitleme, karşılaştırma ve birleştirme, yinelenenler, klasör boyutları, sağlama toplamları, terminal."],
      archives: ["Arşivler", "Arşivleri açma, paketleme, çıkarma ve sınama; disk kalıpları, sanal diskler ve paketler."],
      connections: ["Bağlantılar", "FTP, SFTP, SMB, WebDAV, S3, Azure, bulut sürücüler, telefonlar, PC bağlantısı, RDP, VNC, SSH; vekil sunucular ve kayıtlı parolalar."],
      configuration: ["Yapılandırma", "Yapılandırma penceresinde sayfa sayfa bir tur."],
      themes: ["Renk temaları", "Tema seçmek ve tema düzenleyicisiyle kendi temanızı yapmak."],
      "icon-packs": ["Simge paketleri", "Simge paketi seçmek ve kendi paketinizi yapmak."],
      languages: ["Diller", "Dili seçmek ve NextCommander'ı çevirmek."],
      scripts: ["Makrolar ve betikler", "Makro kaydetme; JavaScript komutları, sütunları ve dosya sistemleri."],
      plugins: ["Eklentiler", "Hazır gelen eklentiler, eklenti kurma ve yönetme, kendi eklentinizi yazma."],
      "settings-folder": ["Ayarlar klasörü", "Ayarlarınızın yeri, yedekler ve geri yükleme, taşınabilir kip."],
    },
  },

  footer: {
    tagline: "Windows, Linux ve macOS için çift panelli dosya yöneticisi.",
    product: "Ürün",
    guides: "Rehberler",
    project: "Proje",
    guideLinks: [["getting-started", "Başlarken"], ["keyboard-shortcuts", "Klavye kısayolları"], ["tools", "Araçlar"], ["connections", "Bağlantılar"], ["plugins", "Eklentiler"], ["configuration", "Yapılandırma"]],
    source: "Kaynak kod",
    releases: "Sürümler",
    issues: "Sorun bildir",
  },

  notFound: {
    title: "Bu sayfa burada değil",
    body: "Adres eski olabilir ya da içinde bir yazım hatası olabilir.",
    back: "Ana sayfaya",
  },

  root: {
    title: "NextCommander",
    choose: "Bir dil seçin",
  },
};
