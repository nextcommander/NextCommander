# Simge paketleri

[README'ye dön](../../README.md)

Simge paketi, NextCommander'ın simgelerinin nasıl görüneceğini belirler: menüler, düğme çubukları, sürücü düğmeleri ve
dosya listesi simgeleri (sistemin kendi dosya simgeleri kullanılmadığında).

## Paket seçme

*Yapılandırma > Seçenekler > Görünüm > Simgeler* bölümünü açın ve bir **Simge paketi** seçin. NextCommander ile üç paket
gelir:

| Paket | Görünüm |
|---|---|
| Renkli | Varsayılan. Dolgulu, renkli simgeler. |
| Tek renk | Her simge tek renkte, renk temasından alınır. Hem açık hem koyu temalara uyar. |
| Fluent | Windows 11'in renkleri: açık bir dolgunun üzerinde ince bir çizgi, köşeleri yuvarlak klasörler ve dosyalar. |

| | | |
|---|---|---|
| ![Renkli](../screenshots/icons-colored.png)<br>Renkli | ![Tek renk](../screenshots/icons-monochrome.png)<br>Tek renk (Koyu tema ile) | ![Fluent](../screenshots/icons-fluent.png)<br>Fluent |

![Simgeler sayfası](../screenshots/configuration-icons.png)

Aynı sayfa ayrıca dosya listesinin hangi simgeleri göstereceğini (örneğin sistemin simgeleri ya da yalnızca
standart olanlar), simge boyutunu ve ana menünün simge gösterip göstermeyeceğini seçer.

## Kendi paketinizi yapma

1. *Görünüm > Simgeler* sayfasında, başlangıç olarak kullanacağınız paketi seçin.
2. Ondan yapılmış yeni bir paket için **Yeni simge paketi...**, değiştirmek için **Simge paketini düzenle...** seçeneğine tıklayın.
3. Simge paketi düzenleyicisinde:
   - **Ad** ve **Simgeler**: listedeki ad ve simgelerin nasıl çizileceği: *Renkli* (dolgulu, renkli),
     *Tek renk* (her şey tek renkte; **Renk temasının simge rengi** ile temayı izler) ya da
     *Çizgi (Fluent)* (açık bir dolgunun üzerinde ince çizgi).
   - **Menü simgelerinin köşesinde küçük işaretler**: artı, çarpı ve ok işaretleri.
   - **Menü ve düğme çubuğu simgelerinin renkleri**: menü simgelerinin çizildiği sekiz renk (mavi, yeşil, kırmızı ...).
   - **Dosya türü simgelerinin renkleri**: klasörler, arşivler, resimler, programlar ve diğer dosya türleri.
   - Bir rengin yanındaki **Varsayılan** yerleşik rengini geri verir. **Önizleme** satırı simgeleri siz değiştirdikçe
     gösterir.
4. **Kaydet**'e tıklayın. Paket listede seçilir; Uygula ya da Tamam'a tıklayın.

Paketleriniz [ayarlar klasöründeki](settings-folder.md) `iconpacks` klasörüne kaydedilir. NextCommander ile gelen bir paketi
düzenlemek kendi kopyanızı oraya kaydeder. Başka bir paketten yapılmış paket simge şekillerini korur (örneğin
Fluent'in yuvarlak klasörleri).
