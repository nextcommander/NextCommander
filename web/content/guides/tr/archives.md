# Arşivler

[README'ye dön](../../README.md)

NextCommander arşivleri klasör gibi açar: içine bakabilir, dosyaları görüntüleyebilir, dışarı kopyalayabilir ve zip
dosyalarını yerinde değiştirebilirsiniz.

![Bir zip arşivinin içi](../screenshots/archive.png)

## Desteklenen biçimler

| Biçim | Aç | Yerinde değiştir | Oluştur (Alt+F5) |
|---|---|---|---|
| zip (ZIP64 ve parola korumalı olanlar dahil) | evet | evet | evet |
| 7z | evet | hayır | 7-Zip kuruluysa |
| rar (RAR5 dahil) | evet | hayır | hayır |
| tar, tar.gz, tar.bz2, tar.xz, tar.zst | evet | hayır | tar, tar.gz, tar.bz2, tar.zst |
| tek dosyalar: .gz, .bz2, .xz, .zst | evet | hayır | hayır |
| disk kalıpları: .iso (Joliet adlı ISO 9660, UDF) | evet | hayır | iso (ISO 9660 + Joliet) |

Eklentiler başka biçimler ekleyebilir. NextCommander ile gelen paketler eklentisi .deb, .rpm, .cab, .msi ve
kapsayıcı kalıbı arşivlerini (docker save, OCI) salt okunur açar; bkz. [Paketler ve yükleyiciler](plugins.md#packages-and-installers).

**Bölünmüş arşivler:** ciltlere bölünmüş bir arşiv (7-Zip'in *ciltlere böl* özelliği ya da bir dosya bölücü
`name.7z.001`, `name.7z.002` ... olarak yazar; `name.zip.001` ve diğer biçimler de) ilk cildinden tek bir arşiv gibi
açılır: `name.7z.001` üzerinde Enter içeriğini gösterir, tüm ciltlerden sırayla okunur (diskte hiçbir şey birleştirilmez).
Bölünmüş arşivler salt okunurdur. Sonraki ciltler kendiliğinden açılmaz.

**Disk kalıpları:** Bir `.iso` dosyasında Enter diskin dosyalarını gösterir: CD kalıpları uzun (Joliet) adlı ISO 9660
dosya sistemiyle, DVD ve Blu-ray kalıpları UDF dosya sistemiyle. Dosyalar her arşivde olduğu gibi görüntülenebilir,
dışarı kopyalanabilir, aranabilir ve karşılaştırılabilir; kalıbın kendisi değiştirilmez.

**Disk kalıbına paketleme:** Paketleme penceresinde (Alt+F5) **ISO** seçin: dosyalar ve klasörler, uzun (Joliet) adlarıyla ve
tarihleriyle bir ISO 9660 kalıbına dönüşür; yazmaya ya da bağlamaya hazırdır. Sıkıştırma yoktur; tek bir dosya en
fazla 4 GB olabilir (daha büyükleri hata verilerek dışarıda bırakılır). Birim etiketi dosya adından gelir (`Backup
2026-11.iso` → `BACKUP_2026_11`).

**Bağlama:** *Dosya > Arşiv > Disk kalıbını bağla* (ya da bir `.iso` dosyasının sağ tık menüsü: Windows'ta Gezgin menüsünün
en üstünde, Shift+sağ tıkta ve Linux / macOS'ta programın kendi menüsünde) imlecin altındaki `.iso` dosyasını sistem
aracılığıyla salt okunur bağlar ve sürücüsünü panelde açar; bağlı bir kalıpta o sürücüye gider. *Disk kalıbının bağlantısını kaldır*
onu kaldırır. Bağlı bir kalıp sıradan bir sürücüdür: her program dosyalarını açabilir ve içindeki bir yükleyici
çalışır. Windows Mount-DiskImage ile (yönetici hakları gerekmez), Linux udisks ile (/media ya da /run/media altında bir
loop aygıtı), macOS hdiutil ile (/Volumes altında) bağlar.

Windows'ta aynı komutlar bir sanal diski (`.vhd`, `.vhdx`) salt okunur olarak, Windows'un okuyabildiği her birimi için
bir sürücü harfiyle bağlar. Windows bunu yalnızca yönetici için yapar: yönetici hakları yoksa önce
sistemin onay penceresi sorar. Böyle bir birimi olmayan disk (Linux bölümleri ...) yeniden bırakılır; onu
Enter ile açın (disk kalıbı eklentisi, bkz. [Eklentiler](plugins.md#virtual-disks)); her sistemde her
sanal diskin dosyalarını okur.

## Arşivi açma

- Arşivin içine girmek için üzerinde **Enter**'a basın. **Backspace** arşivden çıkar, imleç arşiv dosyasında kalır.
- **Ctrl+Page Down** (*Düzen > Arşiv olarak aç*) başka bir adla zip arşivi olan dosyaları da açar; örneğin
  `.docx` ya da `.jar`.
- Yol satırı arşivi yolun bir parçası olarak gösterir; örneğin `C:\Data\project.zip\src`. Böyle bir yolu
  yazabilirsiniz de.
- Arşiv içindeki arşivler de çalışır.
- Arşivin içinde F3 (görüntüle), Ctrl+Q (Hızlı görünüm), dışarı kopyalama ve dosya bulma normal dosyalardaki gibi çalışır.
  Bir dosyada Enter onu kendi programıyla açar.
- Enter'ın arşivleri sisteminizin programıyla açmasını tercih ediyorsanız, [Yapılandırma penceresinin](configuration.md)
  *Paketleyici* sayfasında **Enter arşive girer** seçeneğini kapatın. Ctrl+Page Down yine içeri girer.

## Zip dosyalarını değiştirme

Bir zip dosyasının içinde dosya kopyalayabilir (F5), silebilir (F8), yeniden adlandırabilir ve taşıyabilirsiniz (F6),
klasör oluşturabilir (F7), F4 ile bir dosyayı düzenleyip geri kaydedebilirsiniz. Değişiklikler arşive tek seferde yazılır;
başka bir arşivin içindeki arşivde dıştaki de güncellenir.

Diğer biçimler salt okunurdur.

## Paketleme (Alt+F5)

*Dosya > Arşiv > Paketle...* (sağ tık menüsündeki **Paketle...** de; orada da Windows Gezgini menüsünün en üstünde)
seçili dosyaları diğer paneldeki yeni bir arşive paketler. Pencere en üstte nelerin paketlendiğini ve boyutunu
gösterir, ardından:

- **Arşiv**: yolu (klasör düğmesi başka bir klasör seçer); uzantı biçime göre değişir.
- **Biçim**: ZIP, 7-Zip, TAR, TGZ, TBZ, TZST ve paketleyici eklentileri için kartlar; her biri neye uygun olduğunu söyler; altında
  seçilenin neler yapabildiği (şifreleme, kendiliğinden açılan, ciltler). *Paketleyiciyi yapılandır...* yapılandırmanın
  *Paketleyici* sayfalarını açar. 7-Zip için 7-Zip programı gerekir (aşağıya bakın).
- **Sıkıştırma**: 0'dan (sakla) 9'a (ultra) adıyla birlikte düzey ve arşiv boyutunun, nelerin kazanıldığının ve ne
  kadar hızlı paketlendiğinin **tahmini**. Resimler, medya ve arşivler tahminde boyutunu korur; klasörler
  sayılmaz (boyutları henüz bilinmiyor). TAR'ın düzeyi yoktur.
- **Seçenekler**, her biri açıkken altında ayarlarıyla bir anahtardır:
  - **Alt klasörleri dahil et** (kapalı: seçili klasör dosyalarını getirir ama alt klasörlerini getirmez), **Klasör
    yollarını arşivde koru** ile birlikte (kapalı: her dosya yalnızca kendi adıyla);
  - **Seçili her öğe için bir arşiv**, **Klasörleri paketlerken üst klasörü dışarıda bırak** ile birlikte (bir
    klasörün içeriği arşivinin en üstünde);
  - **Ciltlere böl** (7-Zip): 10 MB, 25 MB (e-posta), 100 MB, 700 MB (CD), 4092 MB (FAT32) ya da yazılan herhangi bir
    boyutta parçalar (`1.5G`; yalnızca sayı megabayttır), kaç parça oluşacağıyla birlikte: `name.7z.001`, `name.7z.002` ...
    7-Zip (ve NextCommander, bkz. [Bölünmüş arşivler](#supported-formats)) takımı ilk parçasından açar;
  - **Kendiliğinden açılan arşiv** (7-Zip, `7z.sfx` modülü varsa; Windows'ta olduğu gibi): kendini açan bir program
    (`name.exe`), 7-Zip olmayan bilgisayarlar için; cilt olarak bölünemez;
  - **Parolayla şifrele**: parola iki kez (göz simgesi gösterir) ve gücü; zip için yöntem (AES-256,
    AES-128 ya da Windows Gezgini'nin açtığı zayıf ZipCrypto), 7z için dosya adlarının da şifrelenip şifrelenmeyeceği (varsayılanlar
    *Paketleyici* sayfalarından gelir);
  - **Paketledikten sonra asıllarını sil**: dosyalar arşive taşınır, yalnızca paketleme başarılı olursa.
- En alttaki satır özetler (ad, biçim, düzey, tahmini boyut, açık seçenekler); **Paketle** (asıllar gidiyorsa *Paketle ve
  sil*), **Kuyruğa ekle** ve **İptal**.
- Var olan bir zip dosyası seçerseniz dosyalar ona eklenir.
- Yarıda kalan bir paketleme işi geride yarım yazılmış arşiv bırakmaz.

### 7z arşivleri

7z arşivleri oluşturmak için [7-Zip](https://www.7-zip.org/) kurun (Linux ve macOS'ta `7zz` ya da `7z`
programı). NextCommander onu kendiliğinden bulur; bulamazsa konumunu *Paketleyici > 7-Zip* sayfasında belirtin. Bu
sayfa ayrıca sıkıştırma yöntemini, katı arşivleri, işlemci iş parçacığı sayısını ve sözlük boyutunu ayarlar ve
paketlemenin ne kadar bellek gerektireceğini gösterir. Parolayla 7z arşivleri dosya adlarını da şifreleyebilir; böylece
içerik parola olmadan listelenemez bile.

![7-Zip sayfası](../screenshots/configuration-7zip.png)

## Arşivden çıkarma (Alt+F9)

*Dosya > Arşiv > Arşivden çıkar...* seçili arşivleri diğer panele çıkarır. Hangi dosyaların çıkarılacağını ve bir dosya
zaten varsa ne yapılacağını kopyalamadaki gibi seçebilirsiniz. Bir arşivin en üstünde birden çok öğe varsa ya da
birkaç arşivi birden çıkarırsanız, her arşiv kendi adını taşıyan ayrı bir klasöre gider.

İnternetten indirilmiş bir arşivden çıkarılan dosyalar sistemin "internetten indirildi" işaretini korur.

## Arşivleri sınama (Alt+Shift+F9)

*Dosya > Arşiv > Arşivleri sına* seçili arşivlerin her dosyasını sonuna kadar okur ve denetler. Bozuk
dosyalar listelenir.

## Parolalar

Bir arşiv şifreliyse NextCommander parolayı sorar, yanlışsa yeniden sorar. Parola program kapatılana kadar
hatırlanır. Tek bir dosya bozuksa ya da parola yanlışsa o dosyayı atlayabilir ya da yeniden deneyebilirsiniz; işin
geri kalanı sürer.

## Güvenlik

Hedef klasörden kaçmaya çalışan arşiv girdileri (`../` ya da mutlak yolla) her zaman hedef klasörün içine
çıkarılır.
