# AeroBag: demo senaryosu

Bir havayolu ya da yer hizmetleri yetkilisine, ya da mülakatta, **7–8 dakikada** gösterilecek akış. Sıra, README'deki üç soruyu izler: yolcu bavulunu ne zaman alacak, bavul uçağa bindi mi, aktarma bavulu yetişecek mi. ExitGuard isteğe bağlı bir ektir.

> Her şey simülasyondur. Bunu açılışta söyleyin; sonraki her rakam "simülasyonda ölçüldü" diye sunulur.

## 1. Hazırlık (gösterimden ~20 dk önce)

Temiz veritabanıyla başlayın. Aynı gün aynı tohumla ikinci kez çalıştırılan simülatör aynı PNR'ları yeniden gönderir, eski bavullarla karışır:

```powershell
scripts\demo -Stop
docker compose down -v      # yalnızca demo verisi silinir; göç aracı şemayı yeniden kurar
scripts\demo                # tam kapsam, hız 5, tohum 42
```

Simülatör penceresi gönderime başlamadan önce üç liste yazar. Bunları bir not dosyasına kopyalayın (gösterim boyunca bu nota bakacaksınız):

| Liste | Kullanıldığı adım |
|---|---|
| `Örnek yolcular` (uçuş başına 2 PNR) | Adım 3: aralık tahmini |
| `Senaryo bavulları` (yüklenmeyecek / varışta indirilmeyecek) | Adım 4: kayıp bavul |
| `Aktarma bavulları (örnek)` (yetişen ve yetişmeyen) | Adım 5: aktarma |

**`Senaryo bavulları` listesi çıkmazsa:** 3 uçuşta (~360 bavul) beklenen senaryo bavulu sayısı ~2'dir; tohuma göre hiç çıkmayabilir. `scripts\demo -Stop`, `docker compose down -v`, sonra başka bir tohumla `scripts\demo -Seed 7` deneyin. Çalışan tohumu bu belgeye yazın.

Tarayıcıda üç sekme açın:

1. `http://localhost:5173/#/`: yolcu sayfası.
2. `http://localhost:5173/#/ops`: `ops` / `ops-demo-2026` ile giriş.
3. `http://localhost:5173/#/ops`: gizli pencerede `security` / `security-demo-2026` ile giriş (jeton sekme oturumunda tutulur; aynı pencerede iki rol karışmasın).

### Zamanlama (hız 5, koddan hesaplandı, ilk provada ölçün)

- Kalkış tarafındaki okumalar (check-in, güvenlik, yükleme, kalkış) açılışta topluca gider. Bu yüzden **yüklenmeyen bavul personel panosuna hemen düşer.**
- Uçuşlar 15 sanal dakika arayla iner: gerçekte ~3 dk arayla. Bir uçuşun indirmesi ~25–30 sanal dakika sürer: gerçekte ~5–6 dk.
- Yüklenmeyen bavulun uyarısı **yolcuya** ancak varışta, "indirme bitti" + 5 dk tolerans geçince görünür: o uçuşun inişinden ~7 dk sonra.
- Kaçırılan bağlantı yolcu kartında ancak bağlantı uçağı kalkınca görünür: inişten 40–150 sanal dk sonra, yani **gerçekte 8–30 dk**. Hazırlığı bu yüzden 20 dk önceden başlatın.

Bu yüzden gösterim iki türlü an içerir: **canlı** (ilerleyen bir uçuş) ve **hazır** (zaten olmuş bir olay). Gösterirken hangisi olduğunu söyleyin.

## 2. Açılış (30 sn)

> "Bavul kaybolduğunda yolcunun başvurduğu ve sorumlu tutulan taraf havayoludur. AeroBag, bir havayolunun bavul takip sisteminin tek istasyon modülü olarak üç soruya cevap veriyor. Bütün veri simülasyondan; gerçek veriyle ne yapmak istediğimi sonda anlatacağım."

## 3. Yolcu bavulunu ne zaman alacak? (2 dk)

1. **Yolcu sekmesi:** son inen uçuşun (listedeki son uçuş) örnek PNR'ı + soyadı. Bavul henüz indirilmediyse kart **uçuş düzeyi tahmin aralığı** gösterir.
   - Söylenecek: "Tek bir saat yerine aralık veriyoruz; P10–P90."
2. **Aynı sekme:** ilk uçuşun PNR'ı. Bavul indirildiyse **bavul düzeyi aralık** daha dardır; bandaysa bant numarası görünür.
   - Söylenecek: "Ampirik yönteme göre ortalama hata 1,6 dakikadan 0,7'ye indi, aralık 5 dakikadan 2,2'ye daraldı."
   - Dürüst kısım: "Yağmurlu, yavaş bir günde model fazla emin kalıyor; kapsama %45'e düşüyor. Biten bavullardan beslenen canlı düzeltme bunu %77'ye getiriyor."
3. **Dil düğmesi → EN:** yabancı yolcu için aynı kart İngilizce.
4. **Ops sekmesi, SLA bölümü:** uçuş başına ilk/son bavul hedefi ve ihlal riski.
   - Söylenecek: "Hedefler demo varsayımı: ilk bavul 15, son bavul 30 dakika."

## 4. Bavul uçağa bindi mi? (2 dk)

1. **Ops sekmesi, "Eksik bavullar":** `NOT_LOADED` uyarısı (hazır an; açılıştan beri orada).
   - Söylenecek: "Yüklenmeyen bavulların hepsi uçak inmeden personel ekranına düşüyor."
2. **Yolcu sekmesi:** `Senaryo bavulları` listesindeki PNR. Varış toleransı geçtiyse kartta sakin dilde "bu uçağa yüklendiğine dair kayıt yok" kutusu; tahmin ve bant gizli.
3. Asıl hikâye, ölçümle verilen karar:
   > "İlk ölçümde bu uyarıların beşte dördü yanlıştı: bavul yüklenmiş, sadece okuyucu kaçırmıştı. Yolcuyu boşuna korkutmamak için yolcuya yalnızca varışta gösteriyoruz. Yolcuya giden yanlış uyarı 1000 bavulda 15,5'ten 1,8'e indi; gerçek vakaların %89'unda yolcu bant boşalmadan haberdar oluyor."
4. Okuma kapsamı (sorulursa): "Çoğu istasyonda bant ağzında okuyucu yok. Sistem bunu yapılandırmadan biliyor ve okuyucu olmayan yerde 'büyük olasılıkla bantta' diyor."

## 5. Aktarma bavulu yetişecek mi? (1,5 dk)

1. **Ops sekmesi, "Aktarma bavulları":** bağlantı uçuşu başına kabul kapanışı, kalan süre, yüklenen ve "yetişmedi" sayaçları.
2. **Yolcu sekmesi:** `Aktarma bavulları` listesinden yetişen bir PNR. İki uçuş tek kartta: son adım "bavulunuzu varış havalimanında alacaksınız".
3. **Yetişmeyen bir PNR** (bağlantı uçağı kalktıysa): "Bagajınız bağlantı uçağınıza yetişmedi; bant başında beklemek yerine bagaj hizmetlerine başvurun."
   - Söylenecek: "Bu olmuş bir olay, o yüzden yolcuya gösteriliyor. Kapanış geçti ama uçak kalkmadıysa yalnızca personel görüyor."
   - Dürüst kısım: "Bağlantıyı kaçırma riskini önceden tahmin etmiyoruz; takip bağlantı uçağına yüklemede bitiyor."

## 6. İsteğe bağlı: ExitGuard (1 dk)

Kapıya cihaz koymak havalimanının kararı olduğu için havayolu görüşmesinde atlanabilir; mülakatta güvenlik tarafını göstermek için iyidir.

1. **Güvenlik sekmesi, "Çıkış inceleme kayıtları":** gerekçe, uçuş, kapı, zaman. Kişisel veri yok.
2. Bir kaydı "Yanlış alarm" diye kapatın.
   - Söylenecek: "Başkasının bavulunu almaların %87'si yakalandı, hiçbiri sahip diye geçmedi. Ama açılan incelemelerin yalnızca dörtte biri gerçek. Taban oran etkisi yüzünden kapıda alarm değil, sessiz inceleme kaydı."

## 7. Kapanış (30 sn)

> "Sınırı açık: bunlar simülasyon sonuçları. Gerçek veriyle yeniden ölçmek istediğim üç şey var: kayıp bavul uyarısının tolerans süreleri, tahmin modelinin sizin istasyonunuzdaki hatası ve okuyucularınızın gerçek kaçırma oranı. Hangi verinin paylaşılabileceğini konuşabilir miyiz?"

## 8. Sık sorulanlar (kısa cevaplar)

| Soru | Cevap |
|---|---|
| Gerçek sistemlere bağlanır mı? | Type B mesajları (BSM, BPM) ve AODB bağlamı alıyor; biçimler gerçeğe benzer ama IATA uyumluluğu iddia edilmiyor. Bağlantı, sahada denenmedi. |
| Yük kaldırır mı? | Yerel makinede 64 eşzamanlılıkta saniyede ~1.800 mesaj, p95 59 ms; yoğun bir havalimanının yoğun saati ~17 mesaj/sn. |
| Yolcu verisi nerede? | Ad, soyad ve PNR hiçbir tabloda, logda ya da adreste düz metin değil; HMAC özeti tutuluyor, saklama süresi dolunca o da siliniyor. |
| WorldTracer ile ilişkisi? | Kapsam dışı; kayıp bavul dosyası süreci havayolunun mevcut sistemindedir. |
| Neden ML? Ortalama yetmez mi? | Ampirik ortalamayla karşılaştırıldı, sonuçlar EVALUATION.md'de; bavula özgü özelliklerin katkı sağlamadığı gibi olumsuz sonuçlar da. |

## 9. Sorun çıkarsa

- **Tahminin dayanağı "geçmiş uçuşların gerçekleşen süreleri" diyor ya da indirme öncesinde hiç tahmin yok:** ML servisi yanıt vermiyor; ops panosundaki ML durum satırına bakın. Gösterime devam edilebilir: indirmeden sonra aralık ampirik yönteme düşer, indirme öncesinde tahmin gösterilmez.
- **Yolcu sorgusu sonuç vermiyor:** PNR'ı simülatör penceresinden yeniden kopyalayın (büyük harf); temiz veritabanı kullanılmadıysa eski bir seyahatle karışmış olabilir.
- **Bir şey takıldı:** `scripts\demo -Stop`, sonra `scripts\demo`. Veri korunur, simülatör baştan gönderir (tekilleştirme kopyaları yok sayar).
