# Renk temaları

[README'ye dön](../../README.md)

## Tema seçme

*Yapılandırma > Seçenekler > Görünüm > Renk* bölümünü açın ve bir **Renk teması** seçin ya da
*Yapılandırma > Renk teması* menüsünü kullanın. NextCommander ile şu temalar gelir:

| Tema | Görünüm |
|---|---|
| Sistemi izle (açık / koyu) | Varsayılan. Sistem açık moddayken sisteminizin renklerini ve vurgu rengini, koyu moddayken Koyu temayı kullanır. Sisteminiz değiştiğinde kendiliğinden geçiş yapar. |
| Sistem renkleri (açık) | Her zaman sisteminizin açık renkleri ve vurgu rengi. |
| Koyu | Visual Studio Code'un "Dark+" renklerinde koyu bir tema. |
| macOS | macOS Finder'ın açık görünümü: mavi vurgu, beyaz liste, mavi klasörler. |
| macOS Koyu | Koyu macOS görünümü. |
| Ubuntu | Açık Ubuntu (Yaru) görünümü: turuncu vurgu, açık gri pencere, turuncu klasörler. |
| Ubuntu Koyu | Koyu Ubuntu (Yaru) görünümü. |

*Yapılandırma > Koyu <-> Normal* (Ctrl+Shift+D) sistem renkleri ile Koyu tema arasında hızlıca geçiş yapar.

| | |
|---|---|
| ![Sistem renkleri](../screenshots/main-light.png)<br>Sistem renkleri | ![Koyu](../screenshots/main-dark.png)<br>Koyu |
| ![Ubuntu](../screenshots/main-ubuntu.png)<br>Ubuntu | ![macOS Koyu](../screenshots/main-macos-dark.png)<br>macOS Koyu |

![Renk sayfası](../screenshots/configuration-color.png)

## Kendi temanızı yapma

1. *Görünüm > Renk* sayfasında, istediğinize en yakın temayı seçin.
2. Geçerli renklerden başlamak için **Yeni tema...**, seçili temayı değiştirmek için **Temayı düzenle...** seçeneğine tıklayın.
3. Tema düzenleyicisinde temaya bir ad verin ve görünümünü seçin (**Açık** ya da **Koyu**).
   - Pencerenin parçaları gruplar halinde listelenir (pencere, denetimler, menüler, dosya listesi ...). Her satırda renk
     (bir seçici ve `#RRGGBB` değeri), neyi renklendirdiği ve anahtarı bulunur. Bir nokta temanın ayarladığı rengi gösterir;
     **Varsayılan** parçayı yeniden açık ya da koyu görünüme bırakır.
   - Bir rengin üzerinde metin okunuyorsa satır kontrastı gösterir (yeşil; olağan en düşük değerin altındaysa "düşük" ile
     kırmızı: metin için 4.5:1, vurgu ve işaretli dosyalar için 3:1).
   - Sağda **canlı önizleme** renklerle bir pencere ve bir dosya paneli gösterir; satırını bulmak için bir parçasına tıklayın.
     Altında tek tıklama bir **vurgu** rengi ayarlar, **Okunabilirlik** geçen kontrast denetimlerini sayar.
   - **Renklerde ara** (Ctrl+F) bir parçayı adıyla ya da anahtarıyla bulur; **Tümünü sıfırla** her parçayı görünüme bırakır.
4. **Kaydet**'e tıklayın. Tema listede seçilir; Uygula ya da Tamam'a tıklayın.

Temalarınız [ayarlar klasöründeki](settings-folder.md) `themes` klasörüne kaydedilir. NextCommander ile gelen bir temayı
düzenlemek kendi kopyanızı oraya kaydeder; kopya, aynı dosya adlı gelen temanın yerine kullanılır.

### Renk anahtarları

Tema düzenleyicisinin listelediği pencere parçaları:


| Anahtar | Neyi renklendirir |
|---|---|
| `Accent` | Vurgu rengi: seçili öğeler, odak çerçevesi, onay kutuları |
| `Window.Background` | Pencerelerin ve iletişim kutularının arka planı |
| `Window.Text` | Genel metin rengi |
| `Window.ErrorText` | Hata iletileri |
| `Controls.Background` | Açılır menülerin, giriş kutularının ve düğmelerin arka planı |
| `Controls.HoverBackground` | Fare altındaki denetimlerin arka planı |
| `Controls.Border` | Denetimlerin kenarlıkları |
| `Controls.SecondaryText` | İkincil metinler ve simgeler |
| `Controls.DisabledBackground` | Devre dışı denetimlerin arka planı |
| `Controls.DisabledText` | Devre dışı denetimlerin metni |
| `Lists.HoverBackground` | Listelerde fare altındaki satır (ayarlar, iletişim kutuları) |
| `Lists.SelectedBackground` | Listelerde seçili satır |
| `MenuBar.Background` | Ana menü çubuğunun arka planı |
| `MenuBar.Text` | Ana menü çubuğunun metni |
| `Menu.Background` | Açılır ve bağlam menülerinin arka planı |
| `Menu.Border` | Açılır ve bağlam menülerinin çerçevesi |
| `Menu.Text` | Menü öğelerinin metni |
| `Menu.HoverBackground` | Fare altındaki menü öğesi (ve açık bir alt menü); belirtilmezse etkin sekmenin rengi (`Tabs.ActiveBackground`). Öğe ayrıca seçili sekme gibi solda bir vurgu çizgisi alır |
| `Menu.HoverText` | Fare altındaki menü öğesinin metni |
| `Menu.GestureText` | Menülerde gösterilen kısayol tuşları |
| `Menu.DisabledText` | Devre dışı menü öğeleri (hiçbir şey seçili değilken seçim gerektirenler de) |
| `ButtonBar.Background` | Düğme çubuğunun arka planı |
| `Drives.Icon` | Sürücü düğmelerindeki simgeler |
| `Tabs.Background` | Klasör sekmesi şeridi |
| `Tabs.ActiveBackground` | Etkin sekme |
| `Tabs.PassiveLine` | Etkin olmayan paneldeki seçili sekmenin soluk alt çizgisi |
| `Panel.HeaderBackground` | Panel başlığı (boş alan satırı) ve küçük resim kutuları |
| `Panel.ActiveAccent` | Etkin panelin işaret rengi |
| `Panel.ActivePathBackground` | Etkin panelin yol satırı |
| `Panel.StatusBackground` | Panelin durum satırı |
| `FileList.Background` | Dosya listesinin arka planı |
| `FileList.AlternateBackground` | Dosya listesinin dönüşümlü satırları |
| `FileList.Text` | Dosya listesinin metni |
| `FileList.MarkedText` | İşaretli (seçili) dosyalar |
| `FileList.HiddenText` | Gizli ve sistem dosyaları |
| `FileList.FolderIcon` | Klasör simgesi |
| `FileList.FileIcon` | Dosya simgesi |
| `FileList.CursorBackground` | İmleç çubuğu (etkin panel) |
| `FileList.CursorText` | İmleç çubuğundaki metin |
| `FileList.InactiveCursorBackground` | İmleç çubuğu (etkin olmayan panel) |
| `FileList.HoverBackground` | Fare altındaki satır |
| `FileList.HeaderBackground` | Sütun başlıkları |
| `FileList.HeaderHoverBackground` | Fare altındaki sütun başlığı |
| `FileList.HeaderText` | Sütun başlıklarının metni |
| `FileList.GridLines` | Kılavuz çizgileri |
| `FunctionKeys.Border` | İşlev tuşları arasındaki çizgi (F3 … F8) |
| `Lister.Background` | Lister (F3) ve düzenleyicinin arka planı |
| `Lister.Text` | Lister (F3) ve düzenleyicinin metni |
| `Compare.Added` | Karşılaştırma: eklenen satırların arka planı |
| `Compare.AddedMark` | Karşılaştırma: değişen bir satırın içinde eklenen karakterler |
| `Compare.AddedText` | Karşılaştırma: + işaretleri ve sağ dosyanın etiketi |
| `Compare.Removed` | Karşılaştırma: kaldırılan satırların arka planı |
| `Compare.RemovedMark` | Karşılaştırma: değişen bir satırın içinde kaldırılan karakterler |
| `Compare.RemovedText` | Karşılaştırma: − işaretleri ve sol dosyanın etiketi |
| `Compare.Changed` | Karşılaştırma: genel bakış şeridinde değişen farklar |
| `Compare.Moved` | Karşılaştırma: taşınan satırların arka planı |
| `Compare.MovedText` | Karşılaştırma: taşınan satırların ↕ işaretleri |
| `Compare.Current` | Karşılaştırma: geçerli farkın çerçevesi |
| `Merge.Left` | Birleştirme: sol tarafın değişiklikleri (arka planlar bundan açılarak yapılır) |
| `Merge.Right` | Birleştirme: sağ tarafın değişiklikleri |
| `Merge.Both` | Birleştirme: iki tarafta da aynı değişiklik, çözülecek bir şey kalmadı |
| `Merge.Conflict` | Birleştirme: çakışmalar |

## Dosya türü renkleri

Temanın yanı sıra dosyalar türüne göre renklendirilebilir: arşivler mor, programlar yeşil, resimler camgöbeği,
medya turuncu, vb. Bunu *Görünüm > Renk* sayfasındaki **Dosya türü renkleri** ile ya da
*Göster > Gösterilenler > Dosya türü renkleri* ile açıp kapatın.

Kurallar aynı sayfadaki **Renk kuralları** kutusunda düzenlenir. Her kuralın şunları vardır:

- **Name:** kuralın adı.
- **Mask:** hangi dosyalara uygulandığı, örneğin `*.zip *.7z *.rar`.
- **LightColor** ve **DarkColor:** açık ve koyu temalarda metin rengi.
- **MaxAgeMinutes** (isteğe bağlı): yalnızca bu kadar dakika içinde değişen dosyalar; örneğin yeni dosyaları vurgulamak için.
- **ExecutableOnly** (isteğe bağlı): yalnızca programlar ve çalıştırılabilir dosyalar.

Bir dosyayla eşleşen ilk kural geçerli olur. Kutu, siz yazdıkça kurallarınızı denetler; bir kuralda hata varken Uygula ve Tamam
reddedilir.

Aynı sayfadaki **Ters seçim**, işaretli dosyaları metin rengi yerine işaret rengini arka plan olarak kullanarak
çizer.
