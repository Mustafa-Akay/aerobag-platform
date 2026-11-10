# AeroBag: gerçek veri planı

Bu belge bir plandır, yapılmış bir işi anlatmaz. Bugün (10 Kasım 2026) elimizde gerçek veri de, sektörden bir bağlantı da yok. Amaç iki katmanlı: proje portfolyo olarak kullanılmaya devam eder; ilgi gelirse aynı adımlar bir pilota dönüşür.

## 1. Neden gerekiyor

Bütün sonuçlar simülasyondan geliyor ve simülatör ile model aynı varsayımlardan doğuyor (`EVALUATION.md`). Üç sonuç doğrudan benim koyduğum bir varsayıma dayanıyor; gerçek veri önce bunları sınar.

| # | Ölçülecek şey | Bugünkü varsayım | Gerçek veri neyi değiştirebilir |
|---|---|---|---|
| 1 | Okuyucuların kaçırma oranı | Mesajların %1'i düşer, %0,5'i bozulur; kayıp bavuldan bağımsızdır | Bozuk okuyucu art arda kaçırır. Oran yüksekse yanlış uyarı sayısı ve tolerans süreleri değişir |
| 2 | Kayıp bavul uyarısının toleransları | Kalkış 10 dk, varış 5 dk; bavulların %0,3'ü yüklenmez, %0,2'si indirilmez | Toleranslar istasyona göre yeniden seçilir; yolcuya giden yanlış uyarı oranı yeniden ölçülür |
| 3 | Tahmin modelinin hatası | Ampirik yönteme göre P50 hatası 1,60 → 0,72 dk; kapsama %79 | Model gerçek veriyle yeniden eğitilir. Yayımlanmış tek gerçek çıpa Münih için ~4–6 dk hata; benim rakamım muhtemelen iyimser |

Sıra bilinçli: 1 numara yalnızca ham okuma kaydı ister ve diğer ikisinin girdisidir.

## 2. İstenecek veri

En küçük işe yarar paket: **tek istasyon, tek gün, varış uçuşları, kimliksizleştirilmiş bavul okuma kaydı.** Bir hafta olursa model de eğitilebilir.

| Veri | Alanlar | Kimde bulunur | Hangi ölçüm için |
|---|---|---|---|
| Bavul okumaları (BPM ya da eşdeğeri) | bavul kimliği, uçuş, okuma noktası, zaman | Yer hizmetleri (bagaj eşleştirme sistemi), havayolu | 1, 2, 3 |
| Bavul listesi (BSM ya da eşdeğeri) | bavul kimliği, uçuş, varsa aktarma bilgisi | Havayolu | 1, 2 |
| Uçuş zamanları | iniş, park, ilk/son bavul, bant | Havalimanı (AODB), yer hizmetleri | 2, 3 |
| Okuyucu yerleşimi | hangi noktada okuyucu var | Yer hizmetleri, havalimanı | 1 (kapsam profili) |
| Kayıp bavul kayıtları (isteğe bağlı) | o gün gerçekten gecikmiş bavulların kimlikleri | Havayolu | 2 (doğru cevap) |

**Kimliksizleştirme veriyi veren tarafta yapılır, bana ulaşmadan önce:**

- Yolcu adı, soyadı ve PNR hiç gönderilmez. Sistem bunlara ölçüm için ihtiyaç duymaz.
- Bavul etiket numarası tutarlı bir rastgele kimlikle değiştirilir (aynı bavul aynı kimliği alır).
- Uçuş numarası ve tarih istenirse maskelenir; gereken yalnızca aynı uçuşun bavullarını gruplamak ve zaman farklarıdır.

Kayıp bavul kayıtları olmadan da 1 ve 3 ölçülür; 2 için yalnızca yanlış uyarı tarafı ölçülür, yakalama oranı ölçülemez.

## 3. Aşamalar

### Aşama 0: hazırlık (bağlantı gerekmez)

- [x] Tek sayfalık özet (`OZET.md`) ve gösterim akışı (`DEMO.md`).
- [ ] 3 dakikalık ekran kaydı: soğuk temasta kimse depoyu kurmaz, video izlenir.
- [ ] Veri istek sayfası: 2. bölümdeki tablo ve kimliksizleştirme kuralları, karşı tarafın BT ya da hukuk birimine iletilebilecek biçimde.
- [ ] İçe aktarma aracı **veri gelene kadar yazılmaz** (yeni özellik yok kararı, 31 Ekim). Gelen dosyanın biçimi görülmeden yazılırsa yeniden yazılır.

### Aşama 1: temas

Hedef kişiler, öncelik sırasıyla:

1. **Yer hizmetleri firmalarında bagaj operasyonu ya da BT** (okuma verisi onlarda ve havayolundan daha erişilebilir).
2. **Havayollarında bagaj hizmetleri ve operasyon kontrol.**
3. **Havalimanı işletmelerinde operasyon ve BT.**
4. **Üniversitelerin havacılık yönetimi bölümleri:** sektörle veri anlaşması olan akademisyenler, ortak çalışma ya da tez verisi yoluyla.

Kanal: LinkedIn ve e-posta ile kısa mesaj + özet + video. İş ve staj başvurularında proje ekte gider; mülakat aynı zamanda temas fırsatıdır. İstek küçük tutulur: önce 20 dakikalık görüşme, veri ikinci adım.

Gerçekçi beklenti: soğuk temasların çoğu cevapsız kalır. 20–30 kişiye yazmadan sonuç hakkında yargıya varılmaz.

### Aşama 2: görüşme

`DEMO.md` akışı (7–8 dk), ardından iki soru: "Bu üç sorudan hangisi sizin için gerçek bir sorun?" ve "Kimliksizleştirilmiş bir günlük okuma kaydı paylaşılabilir mi?" Geri bildirim, veri gelmese de değerlidir; W8 (öncelikli bavul) ve W10b (aktarma riski) gibi ertelenen işlerin sırasını o belirler.

### Aşama 3: veri paylaşımı

- Gizlilik sözleşmesi istenirse imzalanır; veri sahibinin koşulları geçerlidir.
- Veri yalnızca yerel diskte, depo dışında bir klasörde durur. Depoya, CI'a ya da bulut servisine girmez.
- Sonuçlar yayımlanmadan önce veri sahibine gösterilir; istasyon ve firma adı izin olmadan yazılmaz.
- Kimliksizleştirilmiş veri de bavul ve uçuş düzeyinde ticari bilgidir; `KVKK.md`'deki not burada da geçerlidir (hukuki danışmanlık değildir).

### Aşama 4: ölçüm

Hedefler veri gelmeden önce buraya yazılır, sonra ölçülür (projenin kuralı). Şimdiki taslak:

| Ölçüm | Yöntem | Başarı ölçütü (ölçmeden önce) |
|---|---|---|
| 1. Kaçırma oranı | Bavul listesindeki her bavul için beklenen okuma noktalarında okuma var mı; nokta ve saat bazında | Ölçüt yok, bu bir keşif. Çıktı: oran, noktalar arası fark, art arda kaçırma var mı |
| 2. Uyarı toleransları | Mevcut kural (`BagAlertRules`) gerçek kayıt üzerinde, toleranslar taranarak | Yolcuya giden yanlış uyarı 1000 bavulda 5'in altında (simülasyonda 1,8) |
| 3. Tahmin hatası | Gerçek veride uçuş bazlı çapraz doğrulama; ampirik yönteme karşı | Model ampirik yöntemi geçmeli; kapsama %70–90 aralığında. Geçemezse bu da raporlanır |

Sonuçlar `EVALUATION.md`'ye "simülasyon / gerçek" yan yana tablosuyla, olumsuz olanlar dahil yazılır.

### Aşama 5: pilot (yalnızca ilgi gelirse)

Gölge mod: sistem canlı mesajları yalnızca okur, hiçbir yolcuya ya da personele bildirim göstermez; uyarıları gün sonunda gerçek kayıtlarla karşılaştırılır. Bundan önce `IMPLEMENTATION_STATUS.md`'deki açıklar kapanmalıdır (TLS, anahtar yönetimi, tek örnek varsayımı). Bu aşama bugün planlanmıyor.

## 4. Veri hiç gelmezse

Proje bugünkü haliyle geçerli kalır: sınırları açık yazılmış bir simülasyon çalışması. Görüşmelerden gelen geri bildirim yine de kaydedilir. Kamuya açık kaynaklar (SITA, ABD Ulaştırma Bakanlığı, havalimanı performans raporları) yalnızca toplam oran verir, bavul düzeyinde kayıt içermez; bu yüzden üç ölçümün hiçbiri onlarla yapılamaz.

## 5. Açık sorular

- Türkiye'deki istasyonlarda okuma kaydını kim tutuyor ve ne kadar süre saklıyor? (İlk görüşmede sorulacak.)
- Okuma kayıtları Type B biçiminde mi, yoksa bagaj sisteminin kendi dışa aktarım biçiminde mi geliyor?
- Kimliksizleştirilmiş bavul verisinin paylaşımı için firmaların standart bir süreci var mı?
