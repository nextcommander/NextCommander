# Makrolar ve betikler

NextCommander yaptıklarınızı tekrarlayabilir (makrolar) ve JavaScript ile yazılmış küçük programlar (betikler) çalıştırabilir: komut olarak,
dosya listesinin sütunları olarak ya da dosya sistemleri (bir panelde açılan yerler) olarak.

## Makrolar

1. **Araçlar > Makrolar > Makro kaydet** kaydı başlatır. Kayıt sürerken pencere başlığı ● ile başlar.
2. Programı her zamanki gibi kullanın. Bir menüden, tuştan, düğmeden ya da komut satırından çalıştırılan her komut not edilir.
   İmleci hareket ettirmek ya da bir pencerede yazı yazmak komut değildir ve kaydedilmez.
3. **Araçlar > Makrolar > Makro kaydet** yeniden seçildiğinde kayıt durur. Makroya bir ad verin: `uc_<ad>` kullanıcı komutu olarak kaydedilir.

Makroya **Yapılandırma > Komutlar ve kısayollar** bölümünden bir tuş, düğme çubuğunun düzenleyicisiyle de bir düğme verin. Makro,
virgülle ayrılmış bir komut listesidir; bu yüzden parametresi virgül içeren bir komut makroya dahil edilemez; program
kaç komutun dışarıda kaldığını söyler.

**Araçlar > Makrolar**, kaydedilen makroları Makro kaydet'in altında listeler (tuşlarıyla birlikte; tıklamak birini çalıştırır). Sonundaki **Makrolar...**
makrolar penceresini açar: ada, komuta ya da adıma göre arama; sağda seçili makronun komut kimliği (kopyalamak için) ve
adımları sırayla. Enter makroyu çalıştırır; **Sil** pencerede sorar (Kalsın / Sil) ve makroyu tuşları
ve düğme çubuklarındaki düğmeleriyle birlikte siler.

## Komut olarak betikler

Betikler, [ayarlar klasöründeki](settings-folder.md) `scripts` klasörünün `.js` dosyalarıdır.

- **Araçlar > Betikler** onları listeler ve bir tıklamayla birini çalıştırır.
- **Yeni betik...** bir örnek yazar (bir betiğin neleri kullanabileceğini listeler) ve düzenleyicide açar.
- Komut olarak (bir tuş, bir düğme ya da bir kullanıcı komutu için): `nc_RunScript name.js words to pass`. `.js` atlanabilir;
  tam yol başka bir klasördeki betiği çalıştırır.

Betik arka planda çalışır: pencere kullanılabilir kalır. Hatalar satırıyla birlikte gösterilir. `nc.log(...)`, `logs/scripts.log` dosyasına yazar.

| Ne | Kullanım |
| --- | --- |
| Betiğin adından sonraki sözcükler | `nc.args` |
| Programın komutlarını çalıştırma | `nc.run("nc_GoToRoot")`, birkaçı: `nc.run("nc_A, nc_B parameter")`; hepsi biliniyorsa true |
| Etkin / diğer panel | `nc.panel()`, `nc.otherPanel()`: `{ path, cursor, selected, files, folders }` |
| Seçili dosyalar (ya da imlecin altındaki) | `nc.selected()`: tam yollar |
| Bir klasöre gitme, yeniden okuma | `nc.navigate(path)`, `nc.refresh()` |
| Pencereler | `nc.message(text)`, `nc.confirm(text)` (true / false), `nc.prompt(text, value)` (metin ya da null) |
| Bu bilgisayarın dosyaları | `nc.readText(path)`, `nc.writeText(path, text)`, `nc.appendText(path, text)`, `nc.exists(path)`, `nc.list(path)` |

Örnek: seçili dosyaların adlarını diğer paneldeki bir metin dosyasına yazma.

```js
var files = nc.selected();
if (files.length > 0 && nc.confirm("Write " + files.length + " names?")) {
  nc.writeText(nc.otherPanel().path + "/files.txt", files.join("\n") + "\n");
  nc.run("nc_RereadSource");
}
```

## Sütun olarak betikler

`scripts/columns` klasörünün `.js` dosyaları özel sütun takımları için sütun sağlar (**Göster > Görünüm > Görünüm modlarını düzenle...**).
Program onları başlarken okur; bir sütunun alanı `js_<dosya adı>.<id>` biçimindedir. **Araçlar > Betikler > Yeni sütun
betiği...** metin dosyalarının satırlarını sayan bir örnek yazar.

```js
function columns() {                      // the columns: kind "text", "number", "size" or "date"
  return [{ id: "lines", name: "Lines", kind: "number" }];
}
function supports(file) {                 // optional: false = no values for this file
  return !file.isDir && file.isLocal && file.ext === "txt";
}
function value(file, id) {                // file = { name, ext, path, size, modified, isDir, isLocal }
  var text = readText(file.path, 1000000);
  return text === null ? null : text.split("\n").length;
}
```

Bir sütun betiği her değer için 5 saniye alır; başarısız olan değer boş kalır (programın günlüğü betiğin adını söyler).

## Dosya sistemi olarak betikler

`scripts/filesystems` klasörünün `.js` dosyaları bir sunucununki gibi yerlerdir: kayıtlı bağlantılarla birlikte listelenir
(**Bağlantı > Kayıtlı bağlantılar**, *JavaScript* grubu) ve bir panelde açılır; orada kopyalama, taşıma, görüntüleme ve düzenleme her yerdeki gibi
çalışır. Program onları başlarken okur. **Araçlar > Betikler > Yeni dosya sistemi betiği...**, dosyalarını bellekte tutan ve
bir dosya sisteminin sahip olabileceği her işlevi içeren bir örnek yazar.

| İşlev | Ne yapar |
| --- | --- |
| `list(path)` | Bir klasörün girdileri: `[{ name, isDir, size, modified }]` (`modified`: ISO metni ya da milisaniye). Gerekli. |
| `read(path)` | Bir dosyanın içeriği: bir metin ya da `{ base64: "..." }`. Gerekli. |
| `write(path, data)` | Bir dosya yazar (`data.text`, `data.base64`). Yoksa yer salt okunurdur. |
| `mkdir(path)`, `remove(path, isDir)`, `rename(from, to)` | Klasörler, silme, yeniden adlandırma (`rename` yoksa taşıma kopyalar ve siler). |
| `var title = "..."` | Menüdeki ad (yoksa dosyanın adı). |

Yollar `/` ile ayrılır ve `/` ile başlar. Bir sorunu bildirmek için bir `Error` fırlatın: iletisi gösterilir. Program
betiğe şu işlevleri verir:

- `http(method, url, body, headers)` → `{ status, body, headers }` (başlık adları küçük harfle), bir web hizmetinin
  dosyaları için: `http("GET", "https://example.com/api/files", null, { Authorization: "Bearer ..." })`.
- `toBase64(text)`, `fromBase64(base64)`, `readText(localPath, maxChars)`.

Her çağrı 60 saniye alır; bir betiğin aynı anda yalnızca bir çağrısı çalışır.
