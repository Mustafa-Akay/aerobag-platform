# AeroBag: tek sayfalık özet

**Bir havayolunun bavul takip sistemi için tek istasyon modülü.** Bavulu check-in'den banda ya da bağlantı uçağına kadar izler; yolcuya ve operasyona üç sorunun cevabını verir.

> Bu bir ürün değil, çalışan bir portfolyo projesidir. Bütün sonuçlar simülasyondan gelir; gerçek havayolu, havalimanı ya da yolcu verisi kullanılmadı ve IATA uyumluluğu iddia edilmez.

## Neden havayolu

Bavul kaybolduğunda yolcunun başvurduğu ve sorumlu tutulan taraf havayoludur; yolcu verisi (PNR) onun sistemindedir ve IATA Resolution 753 bavul takibini havayollarından ister. Yer hizmetleri ve havalimanı bu kurguda veri sağlayan taraftır.

## Üç soru, ölçülmüş cevaplar

| Soru | Sistem ne yapıyor | Simülasyonda ölçülen |
|---|---|---|
| **Bavul uçağa bindi mi?** | Yüklenmeyen ya da varışta indirilmeyen bavul için uyarı açar; bavul sonradan okunursa uyarı kapanır. | Yüklenmeyen bavulların hepsi uçak inmeden personel ekranında. Yolcuya giden yanlış uyarı 1000 bavulda 15,5'ten 1,8'e indi; gerçek vakaların %89'unda yolcu bant boşalmadan haberdar. |
| **Aktarma bavulu bağlantıya yetişecek mi?** | Gelen ve bağlantı uçuşundaki bavulu birbirine bağlar; kabul kapanışını kaçıranı personele, bağlantıyı kaçıranı yolcuya gösterir. | Akış uçtan uca çalışıyor. Kaçırma riski önceden tahmin edilmiyor (sonraki adım). |
| **Yolcu bavulunu ne zaman alacak?** | Tek bir saat yerine tahmin aralığı (P10–P90) verir; istasyon için ilk/son bavul hedefini ve gecikme riskini izler. | Ampirik yönteme göre ortalama hata 1,60 dakikadan 0,72'ye, aralık 5,0 dakikadan 2,2'ye indi. Yağmurlu, yavaş günde kapsama %45'e düşüyor; canlı düzeltme %77'ye getiriyor. |

**Okuyucu olmayan yerde "bilmiyorum" diyebiliyor.** Çoğu istasyonda bant ağzında okuyucu yoktur. Okuma kapsamı yapılandırmayla verilir; sistem eksik okumayı sıra dışı saymaz ve yolcuya "büyük olasılıkla bantta" der. Kapsamı bilmeyen bir kural 1000 bavulda ~1058 yanlış "sıra dışı" sayardı; bu sistem ~58 sayıyor.

## Nasıl kuruldu

- **Mesajlar:** Type B (BSM, BUM, BPM) sıkı sınırlarla ayrıştırılır; çift gelen tekilleştirilir, sırasız gelen durumu geri almaz, bozuk olan ayrı tabloya düşer.
- **Kişisel veri:** ad, soyad ve PNR hiçbir tabloda ya da kayıtta düz metin durmaz; saklama süresi dolunca arama özetleri de silinir. Tehdit modeli ve KVKK notu depoda.
- **Yük:** yerel makinede saniyede ~1.800 mesaj, p95 59 ms; yoğun bir havalimanının yoğun saati için hesaplanan ihtiyaç ~17 mesaj/sn.
- **Kalite:** 411 otomatik test (gerçek PostgreSQL üzerinde entegrasyon testleri dahil), her gönderimde çalışır.
- **Teknoloji:** .NET 10, PostgreSQL, Python (LightGBM), React. Yolcu ekranı Türkçe ve İngilizce.
- **İsteğe bağlı modül:** çıkış kapısında bavul–PNR eşleştirmesi (ExitGuard), havalimanının kurabileceği ayrı bir parça.

## Sınırı ve sizden istediğim

Sonuçların hepsi simülasyondan; simülatör ile model aynı varsayımlardan doğduğu için rakamlar iyimser olabilir. Gerçek veriyle yeniden ölçmek istediğim üç şey var:

1. Kayıp bavul uyarısının tolerans süreleri,
2. Tahmin modelinin gerçek bir istasyondaki hatası,
3. Okuyucuların gerçek kaçırma oranı.

Kimliksizleştirilmiş bir günlük bavul mesajı kaydı bile bu üçünü ölçmeye yeter. Hangi verinin paylaşılabileceğini ve sistemi 7–8 dakikalık bir gösterimle anlatmayı konuşmak isterim.

**Mustafa Akay** · [github.com/Mustafa-Akay/aerobag-platform](https://github.com/Mustafa-Akay/aerobag-platform) · ayrıntılar: [EVALUATION.md](EVALUATION.md), [DEMO.md](DEMO.md)
