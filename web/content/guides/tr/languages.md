# Diller

[README'ye dön](../../README.md)

NextCommander İngilizce, Türkçe (Türkçe), Almanca (Deutsch), İspanyolca (Español), Fransızca (Français) ve Çince
(中文（简体）, Basitleştirilmiş) olarak gelir. Tek bir metin dosyasıyla başka herhangi bir dil ekleyebilirsiniz.

## Dil seçme

*Yapılandırma > Seçenekler > Görünüm > Dil* bölümünü açın ve listeden bir dil seçin. Her dil ülkesinin bayrağıyla
gösterilir; **Sistem**'in simgesi bir monitördür. Yeni dil Uygula ya da Tamam'a tıklar tıklamaz kullanılır: açık pencereler hemen değişir.

![Dil sayfası](../screenshots/languages.png)

**Sistem** (varsayılan) işletim sisteminizin dilini izler: NextCommander'da o dil için bir dil dosyası varsa
o dil kullanılır, yoksa İngilizce. Türkçe bir sistemde Türkçe, Almanca bir sistemde Almanca alırsınız, vb.

Çince, Çince karakterli bir yazı tipi gerektirir; Windows ve macOS'ta vardır, Linux'ta örneğin
`fonts-noto-cjk` (Debian, Ubuntu) ya da `google-noto-sans-cjk-fonts` (Fedora) kurun.

Dil listesinin gösterebildiği bayraklardan bazıları:

![Dil listesindeki bayraklar](../screenshots/flags.png)

## NextCommander'ı yeni bir dile çevirme

1. *Yapılandırma > Seçenekler > Görünüm > Dil* bölümünü açın ve **Çevir...** düğmesine tıklayın.
2. Dil düzenleyicisinde **Yeni dil...** seçin ve dilin kodunu (iki ya da üç küçük harf,
   örneğin İtalyanca için `it` ya da Brezilya Portekizcesi için `pt-BR`) ve o dildeki adını (örneğin
   *Italiano*) yazın. Bir çeviriyi iyileştirmek için bunun yerine o dili seçin.
3. NextCommander'ın her metni solda İngilizce olarak listelenir, sağda çeviri için bir kutu bulunur.
   **Ara** bir metni bulur, **Yalnızca henüz çevrilmemiş metinler** yapılanları gizler. Sayaç kaç
   metnin çevrildiğini gösterir.
4. **Kaydet**'e tıklayın. Dil, Dil sayfasında seçilir; Uygula'ya tıklayın.

Çeviri, [ayarlar klasöründe](settings-folder.md) `languages/<code>.json` olarak kaydedilir. Oraya kaydettiğiniz bir dil, aynı koda sahip
gelen dilin yerine kullanılır; böylece gelen çevirileri de bu yolla iyileştirebilirsiniz. Bir çeviride İngilizce metindeki bir yer tutucu eksikken kaydetme reddedilir (aşağıya bakın).

### İpuçları

- **Her metnin altındaki küçük gri ad nerede kullanıldığını söyler.** `Menu.nc_...` menü öğeleri,
  `Command.nc_...` komut açıklamaları (komut listesinde ve düğme ipuçlarında), `Settings.<Page>.<Setting>` Yapılandırma
  penceresindeki etiketlerdir (altlarındaki yardım metni için `.Description`), diğer adlar ise metni kullanan pencereyle başlar;
  örneğin `CopyDialog.` ya da `ListerView.`.
- **Yer tutucuları koruyun.** `{0}`, `{1}` vb. bir dosya adı ya da bir sayı gibi değişen parçaların yerini tutar.
  Çevirinizde onları koruyun; cümlenizde uygun yere taşıyabilirsiniz. `{0:N0}` ve
  benzer biçimler, biçimlendirilmiş sayılar ya da tarihlerdir; olduğu gibi kopyalayın.
- **Çevrilmemiş metinler İngilizce kalır.** Bir kısmını çevirip deneyebilir, sonra devam edebilirsiniz.
- Dosya ve klasör adları asla çevrilmez.

NextCommander güncellendikten sonra dilinizi düzenleyicide açın ve eklenen metinleri bulmak için **Yalnızca henüz çevrilmemiş metinler** kutusunu işaretleyin.
