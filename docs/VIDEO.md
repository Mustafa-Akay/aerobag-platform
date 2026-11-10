# AeroBag: 3 dakikalık ekran kaydı

Soğuk temasta gönderilecek kısa video için çekim akışı. Uzun gösterim için [DEMO.md](DEMO.md)'ye bakın.

## 1. Hazırlık (kayıttan 20 dakika önce)

Temiz veritabanıyla, kayıp bavul senaryosu olan tohumla açın:

```powershell
scripts\demo -Stop
docker compose down -v
scripts\demo -Seed 1
```

20 dakika bekleyin: "bağlantıya yetişmedi" kartı ancak bağlantı uçağı kalkınca görünür (açılıştan ~17 dk sonra).

Tarayıcıda yalnızca iki sekme açık olsun:

1. `http://localhost:5173/#/`: yolcu sayfası.
2. `http://localhost:5173/#/ops`: `ops` / `ops-demo-2026` ile giriş.

Tam ekran (F11) yapmayın; sekme çubuğu görünsün. PNR'ları kâğıda ya da telefona yazın, ekranda not dosyası açmayın.

## 2. Geçişler

- **Ctrl+1** yolcu sekmesi, **Ctrl+2** operasyon sekmesi.
- Operasyon panosunda bölümler arasında fare tekerleğiyle yavaş kaydırın; "Eksik bavullar" ve "Aktarma bavulları" başlıklarında durun.
- Yeni yolcu: PNR kutusuna tıklayın, **Ctrl+A**, yeni PNR, Tab, soyad, Enter.

## 3. Kayıt

**Win+Shift+R** (Ekran Alıntısı Aracı): tarayıcı penceresini çerçeveye alın, mikrofonu açın, "Başlat". Bitince "Durdur" ve MP4 olarak kaydedin.

## 4. Akış (tohum 1)

| Süre | Sekme | Ne yapılır | Ne söylenir |
|---|---|---|---|
| 0:00–0:20 | Yolcu | Boş sayfa | "AeroBag, bir havayolunun bavul takibi için tek istasyon modülü. Üç soruya cevap veriyor. Bütün veri simülasyon." |
| 0:20–0:55 | Yolcu | `5743AR` / `OZTURK`, sonra EN ve geri TR | "Yolcu bavulunu ne zaman alacak: tek bir saat yerine bir aralık veriyoruz." |
| 0:55–1:35 | Yolcu | `DT2QGZ` / `OZTURK` | "Bu bavul uçağa yüklenmedi. Yolcuya ancak varışta söylüyoruz, çünkü ilk ölçümde uyarıların beşte dördü okuyucu kaçırmasıydı." |
| 1:35–2:00 | Ops | "Eksik bavullar" | "Personel aynı bavulu uçak inmeden görüyor." |
| 2:00–2:30 | Ops | "Aktarma bavulları" | "Bağlantı uçuşu başına kabul kapanışı ve yetişmeyen bavullar." |
| 2:30–2:50 | Yolcu | `MPEKNV` / `KAYA` | "Yetişmeyen bavulun yolcusu bant başında beklemek yerine bagaj hizmetlerine yönlendiriliyor." |
| 2:50–3:00 | Yolcu | Kart ekranda kalır | "Bunlar simülasyon sonuçları. Gerçek veriyle yeniden ölçmek istiyorum." |

`5743AR` üçüncü uçuşun yolcusudur. Kayıt anında bavul banda çıkmışsa aralık görünmez; simülatör penceresindeki `Örnek yolcular` listesinden tahmin aralığı gösteren başka bir yolcu seçin.

Bu akış 10 Kasım 2026'da yazıldı ve henüz kayıtla denenmedi; ilk provada süreleri ölçüp bu tabloyu düzeltin.

## 5. Bitince

```powershell
scripts\demo -Stop
```
