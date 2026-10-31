# AeroBag: Sektör uyumu düzeltme planı (Faz 6)

Bu plan, 27 Ekim 2026'daki eleştirel incelemenin bulgularını öncelik ve mimari katmana göre iş paketlerine çevirir. Amaç, sistemi bir havayolu ya da havalimanı operasyon uzmanının ilk soracağı sorulara cevap verebilir hale getirmektir. Fazlar 1-4 tamamdır; Faz 5 (RFID donanım demosu) isteğe bağlıdır ve bu planın dışındadır.

Plan bir çalışma belgesidir: her paket bitince `IMPLEMENTATION_STATUS.md`'ye işlenir ve buradaki durum güncellenir.

## 1. Bulgular ve öncelik

Önceliği iki soru belirler: **sektör için ne kadar önemli** (bir operasyon uzmanı bunu sorar mı?) ve **başka işleri engelliyor mu** (temel mi?).

| # | Bulgu | Sektör etkisi | Temel mi? | Öncelik |
|---|---|---|---|---|
| B1 | Kayıp bavul (yüklenmedi / varışta yok) tespit edilmiyor; yolcuya "bavulunuz bu uçakta değil" denemiyor | Çok yüksek: havayolunun en pahalı bavul sorunu | Hayır | **P0** |
| B2 | Simülatör sahada çoğu zaman olmayan okumalara (bant ağzı, banttan alma) dayanıyor | Çok yüksek: ETA, SLA ve çıkış süresi bu varsayıma bağlı | Hayır | **P0** |
| B3 | Bant numarası tutulmuyor ve gösterilmiyor | Yüksek: yolcunun salonda ilk baktığı bilgi | Hayır | **P0** (küçük iş) |
| B4 | "Şimdi" kavramı 5 yerde "en son olay zamanı" olarak hesaplanıyor (sanal saat üretim koduna sızmış) | Orta: gerçek kurulumda yanlış sonuç verir | **Evet**: yeni işçiler (B1) de saat ister | **P1, önce yapılır** |
| B5 | Şema göçü yok; her değişiklik bütün veriyi siliyor | Orta: üretimde kabul edilemez | **Evet**: B1-B3 şemayı değiştirecek | **P1, önce yapılır** |
| B6 | .NET ile ML servisi arasında sözleşme testi yok | Orta: sessizce ML'siz çalışmaya düşebilir | Hayır | P1 |
| B7 | Yük ve ölçek bilinmiyor | Orta: "saatte kaç mesaj?" sorusunun cevabı yok | Hayır | P1 |
| B8 | Öncelikli bavul (business, priority etiketi) yok; kabin sınıfı okunup kullanılmıyor | Orta | Hayır | P2 |
| B9 | Arayüz yalnızca Türkçe | Orta (Antalya'da yolcuların çoğu yabancı) | Hayır | P2 |
| B10 | Aktarma bavulu yok (IATA 753'teki dört izleme noktasından biri) | Yüksek ama büyük iş | Hayır | P2 |
| B11 | ExitGuard'ın operasyon maliyeti (kuyruk, personel yükü) değerlendirilmedi | Orta | Hayır | P3 |
| B12 | Tekrarlanan SQL yardımcıları, belge yükü | Düşük | Hayır | P3 |

## 2. Dalgalar

```
Dalga 0 (temel)      W1 Şema göçü ──┐
                     W2 Saat soyutlaması ──┤
                                           ▼
Dalga 1 (sektör P0)  W3 Bant numarası ─▶ W4 Kayıp bavul tespiti ─▶ W5 Okuma kapsamı profilleri
                                           │
Dalga 2 (güven P1)   W6 ML sözleşme testi   W7 Yük testi
                                           │
Dalga 3 (genişleme)  W8 Öncelikli bavul   W9 İngilizce arayüz   W10 Aktarma bavulu
                     W11 ExitGuard maliyet analizi   W12 Temizlik ve belge sadeleştirme
```

**Dalga 3 için kararlaştırılan sıra (30 Ekim, 31 Ekim'de güncellendi):** W12a (yolcu jetonunun kapsamı, güvenlik) → W9 → W10a → W12'nin kalanı → toparlama (demo senaryosu, README, tek sayfalık özet, gerçek veri planı). W8 bu sıranın sonunda yeniden değerlendirilir (aşağıda).

Dalga 0 önce yapılır, çünkü Dalga 1'deki her paket şemayı değiştirecek ve yeni zamanlanmış işler ekleyecek. Bunlar göç ve saat soyutlaması olmadan yapılırsa aynı borç büyür.

## 3. İş paketleri

Her paket için: mimarideki yeri (Domain / Application / Infrastructure / API / Simülatör / Arayüz), veri modeli değişikliği, bitti ölçütü ve testler.

### Dalga 0: temel

#### W1. Şema göçü (B5)
- **Mimari:** `db/migrations/0001_init.sql`, `0002_...` sırayla uygulanır; `schema_migrations` tablosu hangi dosyanın uygulandığını tutar. Göçleri **tablo sahibi** hesabıyla çalışan ayrı bir araç uygular (`AeroBag.Migrator` ya da API açılışında ayrı bağlantı); uygulama rolü DDL yapamamaya devam eder.
- **Veri modeli:** bugünkü `init.sql` ilk göç olur. `docker-compose` artık yalnızca boş veritabanı kurar.
- **Bitti ölçütü:** yeni bir göç eklenip API yeniden başlatıldığında mevcut veri korunur. Uygulanmış bir göç dosyası değiştirilirse araç hata verir (sağlama toplamı).
- **Testler:** boş veritabanına bütün göçler; yarım veritabanına kalan göçler; değiştirilmiş göçte hata; testlerin fikstürü de göçleri kullanır.

#### W2. Saat soyutlaması (B4)
- **Mimari:** Application'da `IOperationalClock` ("operasyonun şimdisi"). İki uygulama: `SystemOperationalClock` (gerçek saat) ve `EventDerivedClock` (simülasyon: en son olay zamanı). Seçim yapılandırmayla (`Clock:Mode=real|event`, varsayılan `event` çünkü demo simülasyonla çalışıyor).
- **Değişen yerler:** `ExitDeadlineService`, `FlightContextStore`, `OpsQueries`, `PassengerQueries`, `SlaQueries`. SQL içindeki `max(occurred_at_utc)` alt sorguları kalkar, saat parametre olarak verilir.
- **Bitti ölçütü:** kodda `max(occurred_at_utc)` ile "şimdi" hesaplayan yer kalmaz. İki modda da testler geçer.
- **Testler:** her sorgu sabit saatle test edilebilir hale gelir (bugün testler "şimdi"nin ortak olmasıyla uğraşıyor, `SlaQueriesTests` buna örnek).

### Dalga 1: sektör P0

#### W3. Bant numarası (B3)
- **Mimari:** AODB bağlamına `carousel` alanı (simülatör zaten biliyor, yalnızca göndermiyor). Bant değişikliği bağlam güncellemesiyle gelir ve canlı kanaldan bildirilir.
- **Veri modeli:** `flight_context.carousel`.
- **API ve arayüz:** yolcu sorgusu yanıtında `carousel`; yolcu sayfasında "Bant 4" (bant değişirse güncellenir); operasyon tablosunda bant sütunu.
- **Bitti ölçütü:** yolcu, bavulu indirildiği andan itibaren bant numarasını görür.
- **Testler:** bağlamda bant varsa yanıtta var; bant değişince yeni değer döner.

#### W4. Kayıp bavul tespiti (B1)
- **Domain:** kayıp bavul bir **durum değil, uyarıdır** (durum makinesi monoton kalır; bavul sonradan başka uçakla gelebilir). Yeni kavram: bavul uyarısı (`NOT_LOADED`, `MISSING_AT_ARRIVAL`).
- **Application (saf kurallar):**
  - `NOT_LOADED`: BSM var, uçuş kalkmış (AODB'den kalkış zamanı) ve kalkıştan sonra belirli bir süre içinde yükleme okuması yok.
  - `MISSING_AT_ARRIVAL`: uçuşta indirme tamamlandı (AODB'den "indirme bitti" ya da son bavul zamanı) ve bu bavul indirilmedi.
  - Tolerans süreleri yapılandırılabilir ve demo varsayımı olarak işaretlenir.
- **Infrastructure:** `bag_alerts` tablosu (tür, açılış, kapanış, gerekçe); uyarıyı açan zamanlanmış iş (W2'deki saatle). Bavul sonradan okunursa uyarı kapanır ("bulundu").
- **Veri modeli:** `flight_context.departed_utc` (kalkış), `flight_context.unloading_completed_utc`; `bag_alerts`.
- **API ve arayüz:** yolcu sayfasında "Bavulunuz bu uçağa yüklenmedi / bu uçuşla gelmedi. Bant beklemenize gerek yok, kayıp bavul ofisine başvurun." (alarm dili yok). Operasyon panosunda "Eksik" sütunu ve uyarı listesi.
- **Simülatör:** yüklenmeyen (%0,5) ve varışta bulunmayan (%0,3) bavul senaryoları, demo varsayımı. ABD Ulaştırma Bakanlığı'nın yayımladığı yanlış işlenen bavul oranı mertebe karşılaştırması için kaynak olarak eklenir.
- **Ölçüm:** tespit oranı, yanlış uyarı oranı ve tespit gecikmesi (bavul gerçekte kaçtıktan kaç dakika sonra yolcuya söylendi).
- **Bitti ölçütü:** yüklenmeyen bavulun yolcusu, uçak inmeden önce uyarı görür. Varışta bulunmayan bavulun yolcusu bant boşalmadan uyarı görür.
- **Ölçütte değişiklik (W4c ölçümünden, 29 Ekim):** kalkıştaki NOT_LOADED uyarılarının ~5'te 4'ü kaçırılan yükleme okumasından doğan yanlış uyarıydı ve varışa kadar (~3,5 saat) açık kalıyordu. Karar: NOT_LOADED'ı **personel** kalkıştan sonra görür (bavulu sonraki uçağa yönlendirmek onların elindedir); **yolcu** her iki uyarıyı da varışta, bant boşalmadan görür. Yeni ölçüt: gerçek vakaların büyük çoğunluğunda yolcu bant boşalmadan haberdar olur ve yolcuya giden yanlış uyarı, personel ekranındakinin küçük bir kesridir (EVALUATION.md).
- **Testler:** kural birim testleri (sınır süreler, sonradan bulunan bavul), işçi entegrasyon testleri, yolcu yanıtı, simülatör senaryoları.
- **Kapsam dışı (belgelenir):** WorldTracer ya da kayıp bavul raporu (PIR) entegrasyonu, kurye ve teslimat süreci.
- **W3 canlı denemesinden not (28 Ekim):** ZZ0102'de bavullar on-block'tan 30 dakikadan uzun süre sonra hâlâ uçaktaydı. Uçuş düzeyi ML penceresi geçtiği için kasıtlı olarak gizlendi ve yolcu hiçbir tahmin görmedi. Bu "beklenenden geç" durumu da yolcuya söylenmeli (ör. "Bavullar beklenenden geç indiriliyor"), operasyon panosunda gecikme olarak görünmeli. Kayıp bavul uyarısına giden ilk aşama budur.

#### W5. Okuma kapsamı profilleri (B2)
- **Simülatör:** üç profil.
  - `full`: bugünkü durum.
  - `typical`: bant ağzı ve banttan alma okuması yok, şut okuması var, AODB "ilk bavul bantta / son bavul bantta" zamanlarını uçuş düzeyinde gönderir.
  - `minimal`: yalnızca BSM, yükleme ve indirme.
- **Sistemin uyumu:**
  - **ETA:** bant okuması yoksa bavul "indirildi" ya da "şutta" durumunda kalır. Tahmin penceresi geçince yolcuya "bavulunuz büyük olasılıkla bantta" denir. Bu türetilmiş bir bilgidir, durum değişmez.
  - **SLA:** ilk ve son bavul, bant okuması yoksa AODB'nin uçuş düzeyi zamanlarından gelir. İkisi de yoksa SLA "veri yok" olur.
  - **Çıkış süresi:** banttan alma okuması yoksa başlatılmaz. Kapı okuması (ExitGuard) tek çıkış bilgisi olur.
- **Ölçüm:** her profilde ETA doğruluğu ve kapsama, SLA'nın hesaplanabildiği uçuş oranı, takılı bavul tespiti. `EVALUATION.md`'ye "okuma kapsamına duyarlılık" bölümü.
- **Bitti ölçütü:** `typical` profilde hiçbir ekran yanlış bilgi göstermez. Bilinmeyen yerde "bilinmiyor" ya da "büyük olasılıkla" der.
- **Testler:** profil başına simülatör testleri; okuması olmayan bavul için türetilmiş durum ve SLA'nın AODB zamanına düşmesi.

### Dalga 2: güven

#### W6. ML sözleşme testi (B6)
- **Mimari:** `contracts/ml/` altında örnek istek ve yanıt JSON'ları. Python testi, servisin gerçek yanıtının bu örneklerle aynı alanları ve tipleri taşıdığını doğrular. .NET testi, `MlHttpPredictor`'ı sahte bir HTTP işleyiciyle aynı dosyalardan besler (bugün bu sınıfın hiç testi yok).
- **Bitti ölçütü:** Python'da bir alan adı değişirse iki taraftan biri kırmızıya döner.
- **Yapılan (30 Ekim):** sözleşme dosyaları `contracts/ml/`; Python ve .NET sözleşme testleri; `MlStatus`, hata logu, `GET /v1/ml/status` ve operasyon panosunda durum satırı; `Ml:TimeoutSeconds`.
- **Görünürlük (W3 canlı denemesinden, 28 Ekim):** ML servisi `200` dönerken yolcu sorgusu indirme aşamasında `HISTORY`'ye düştü. En olası neden 2 sn zaman aşımı ve ardından 15 sn bekleme, ama `MlHttpPredictor` hataları hiçbir yere yazmadığı için doğrulanamadı. Hatalar ve bekleme başlangıcı uyarı olarak loglanır (servis adresi ve süre ile, kişisel veri olmadan); operasyon panosunda "ML erişilemedi" sayacı ve son hata zamanı gösterilir; zaman aşımı yapılandırılabilir olur.

#### W7. Yük testi (B7)
- **Mimari:** simülatöre `--mode load`: imzalı mesajları eşzamanlı gönderir; saniyedeki mesaj sayısı, p50/p95/p99 gecikme ve hata oranı raporlanır.
- **Ölçüm:** hedef iş yükü yaygın bir havalimanının yoğun saatine göre seçilir ve kaynakla gerekçelendirilir. Darboğaz (bavul başına kilit, outbox, indeksler) tespit edilir.
- **Bitti ölçütü:** `EVALUATION.md`'de "bu makinede, bu ayarlarla saatte N mesaj, p95 X ms" ve darboğazın ne olduğu yazılı.

### Dalga 3: genişleme

#### W8. Öncelikli bavul (B8)
- BSM'den öncelik (kabin sınıfı ya da öncelik işareti). Simülatörde öncelikli bavullar indirme sırasında öne alınır. ETA'da öncelik özelliği. Ölçüm: öncelikli bavullar için ETA hatasının özellikli ve özelliksiz karşılaştırması. Simülatör ürettiği için sonuç döngüseldir, bu açıkça yazılır.
- **Ertelendi (30 Ekim):** bavulların indirme sırasını yer hizmetleri belirler; AeroBag bu sırayı değiştirmez, yalnızca izler. Öncelik etiketinin simülatörde öne alınması, kendi ürettiğimiz sonucu ölçmek olur. Paket, diğer paketler bittikten sonra "öncelik sözü tutuluyor mu" denetimi (öncelikli bavulların gerçekten önce banda gelip gelmediğini ölçen bir gösterge) olarak yeniden düşünülecek.

#### W9. İngilizce arayüz (B9)
- Yolcu sayfası ve bildirim metinleri için TR/EN sözlüğü ve dil seçici. Tarayıcı diline göre varsayılan. Personel ekranları ikinci adımda.

#### W10. Aktarma bavulu (B10)
- **Neden:** SITA 2026 Baggage IT Insights'a göre 2025'te yanlış yönetilen bavulların %39'u aktarma kaynaklıdır (bir önceki yıl %41); tek başına en büyük neden. Antalya ağırlıklı olarak doğrudan uçuş alan bir havalimanıdır; aynı model bağlantılı uçuşları olan her havayolu ve hub için geçerlidir.
- **W10a, veri ve akış:** aktarma bavulu iki bağlı satır (gelen uçuş ve bağlantı uçuşu; her biri yine `etiket@uçuş`). BSM'de `.I` (gelen uçuş) ve `.O` (bağlantı uçuşu), yeni okuma noktası `TRANSFER`. Durumlar `TRANSFER_SORTED` (bağlantı satırı) ve `TRANSFERRED` (gelen satır aktarmaya devredildi). Bağlantı uçuşunun planlanan kalkışı AODB kilometre taşıyla gelir; bavul kabul kapanışı ondan hesaplanır (demo varsayımı 15 dk). Yolcu kartı tek yolculuk; operasyon panosunda "Aktarma" sütunu ve bağlantı uçuşu bölümü. Simülatörde aktarma bavulları ve 20 dk'lık kalkış tarifesi.
- **W10b, risk ve ölçüm (sonraya bırakıldı, 31 Ekim):** "bağlantıyı kaçırma riski" uyarısı (yalnızca personel; kalan aktarma süresinin kötümser tahmini kabul kapanışını geçiyor), uyarı tablosuna yazılan "bağlantı kaçırıldı", acil aktarma listesi ve `transfer-eval` modu. Ölçmeden önce yazılan hedef: kaçırılan bavulların en az %80'i kabul kapanışından en az 10 dk önce işaretlenmeli. W10a'ya yalnızca doğruluk düzeltmesi alındı: bağlantı uçağı kalktıysa ve bavul yüklenmediyse yolcu kartı bunu söyler (aksi halde "yüklenecek" demeye devam ediyordu).
- **Sonraya bırakıldı (31 Ekim):** bağlantı uçuşunun varış tarafını izlemek (varışta indirme, bant, alındı). Bu kurulum aktarma havalimanındadır; varış tarafı başka bir havalimanının verisidir. Bir havayolu kurulumunda havayolları arası bavul mesajlarıyla gelir (IATA Resolution 753 dört noktada izleme ister, sonuncusu varışta yolcuya teslim). Bağlantı satırının durum zinciri bu adımları zaten içerir; eksik olan simülatör ve yolcu kartıdır. Toparlama aşamasında, sistem bir havayoluna da gösterilecekse ele alınacak. Şimdilik kartın son adımı yolcuya bavulunu hangi havalimanında alacağını söyler.

#### W11. ExitGuard operasyon maliyeti (B11)
- Kapı başına saatlik inceleme sayısı, personel süresi ve kuyruk etkisi için basit bir model. "Bu kapı hangi koşulda kurulmaya değer?" sorusuna eşik tablosu. Varsayılanlar açıkça demo varsayımı olarak işaretlenir.
- **Kapsam dışı (30 Ekim):** çıkış kapısına cihaz ya da personel koymak havalimanı işletmesinin kararıdır; sistemi kullanacak bir havayolu ya da yer hizmetleri şirketi bu maliyeti taşımaz ve bu soruyu sormaz. ExitGuard kodda, havalimanı tarafında isteğe bağlı bir modül olarak kalır.
- **İleride düşünülebilecek, sınırıyla:** yolcunun bant başında kendi telefonuyla bavul etiketini okutup "bu benim bavulum" doğrulaması yapması. Cihaz ve personel gerektirmez, okutma bir "bavul sahibine ulaştı" kaydı da olur. Sınırı: gönüllüdür; yanlış bavulu alan yolcu onu kendi bavulu sandığı için okutmaz, yani en çok yakalanması gereken durumu kaçırır. Okutmanın yokluğu bir şey ifade etmez, uyarıya dayanak olamaz.

#### W12. Temizlik ve belge sadeleştirme (B12)
- Ortak veritabanı yardımcıları tek yerde. `IMPLEMENTATION_STATUS.md` ikiye bölünür: kısa ve güncel bir durum belgesi ile ayrı bir değişiklik günlüğü (`CHANGELOG.md`).
- **W12a. Yolcu jetonunun kapsamı (W4 canlı denemesinden, 29 Ekim):** jeton bavulu yalnızca etiket numarasıyla tanır; etiketler zamanla yeniden kullanılır. Jeton `etiket@uçuş` kapsamına alınır; `/v1/bags/{tag}` ve canlı kanal grupları da uçuşla birlikte çalışır. Güvenlik açığı olduğu için W12'nin kalanından ayrılıp Dalga 3'ün başına alındı.

## 4. Çalışma kuralları

- Her paket kendi testleriyle gelir; test geçmeden sonraki pakete geçilmez.
- Her paketin sonunda belgeler (`IMPLEMENTATION_STATUS.md`, gerekiyorsa `EVALUATION.md` ve `THREAT_MODEL.md`) güncellenir ve commit atılır.
- Yeni varsayımların hepsi (oranlar, süreler) kodda tek yerde toplanır ve "demo varsayımı" olarak işaretlenir.
- W1 bittikten sonra şema değişiklikleri `docker compose down -v` gerektirmez.

## 5. Durum

| Paket | Durum |
|---|---|
| W1 Şema göçü | Tamam (PR birleştirildi) |
| W2 Saat soyutlaması | Tamam (PR birleştirildi) |
| W3 Bant numarası | Tamam (PR birleştirildi) |
| W4 Kayıp bavul tespiti | Tamam (PR'lar birleştirildi) |
| W5 Okuma kapsamı profilleri | Tamam (PR birleştirildi) |
| W6 ML sözleşme testi | Tamam (PR birleştirildi) |
| W7 Yük testi | Tamam (PR birleştirildi) |
| W8 Öncelikli bavul | Ertelendi (sıranın sonunda yeniden değerlendirilecek) |
| W9 İngilizce arayüz | Yolcu ekranı tamam (PR birleştirildi); personel ekranları ikinci adım |
| W10a Aktarma bavulu: veri ve akış | Kod ve testler hazır, PR bekliyor (branch `faz6/w10a-transfer`) |
| W10b Aktarma bavulu: risk ve ölçüm | Sonraya bırakıldı (yeni özellik yok kararı, 31 Ekim) |
| W11 ExitGuard maliyeti | Kapsam dışı (gerekçe yukarıda) |
| W12a Yolcu jetonunun kapsamı | Tamam (PR birleştirildi) |
| W12 Temizlik ve belgeler (kalanı) | Başlamadı |
