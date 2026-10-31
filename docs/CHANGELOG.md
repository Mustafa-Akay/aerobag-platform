# AeroBag: Değişiklik günlüğü

Her iş paketinin ne yaptığı, neden yaptığı, bilinen sınırları ve testleri. En yeni en üstte. Kodun **bugünkü** durumu için [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md)'ye bakın; faz ve paket numaraları [ARCHITECTURE.md](ARCHITECTURE.md) bölüm 1.1 ve [REMEDIATION_PLAN.md](REMEDIATION_PLAN.md)'dedir.

Bu girdiler yazıldıkları andaki durumu anlatır; sonraki bir paket bir sınırı kapatmış olabilir (örneğin W4'te "W12'ye not edildi" denen jeton kapsamı W12a'da kapandı). W1 öncesindeki girdilerdeki "şema değişti, `docker compose down -v` gerekir" notları kaldırıldı: şema artık göçlerle güncellenir, veri korunur.

## Toparlama, T1: havayolu çerçevesi

- **Karar (31 Ekim):** AeroBag bir havalimanı platformu olarak değil, bir **havayolunun bavul takip sisteminin tek istasyon modülü** olarak konumlanır. Gerekçe: bavul kaybolduğunda sorumlu olan ve yolcunun başvurduğu taraf havayoludur, PNR onun sistemindedir, IATA 753 takibi havayollarından ister. Yer hizmetleri ve havalimanı veri sağlayan taraftır.
- **Cevap verilen üç soru:** bavul uçağa bindi mi; aktarma bavulu bağlantıya yetişecek mi; yolcu bavulunu ne zaman alacak. İlk/son bavul teslim hedefi üçüncü sorunun istasyon tarafıdır.
- **Değişen:** README girişi ve öne çıkanların sırası (kayıp bavul uyarısı ve okuma kapsamı öne; ExitGuard "isteğe bağlı modül" olarak sona). `ARCHITECTURE.md` v2.2: konumlandırma ve taraflar tablosu, amaçlara kayıp ve bağlantıyı kaçıran bavul, kapsam dışına çok istasyonlu ağ ve kayıp bavul dosyası süreci. Kod değişmedi.
- **Ertelenen:** istasyon boyutu, aktarmanın varış tarafı ve W10b; yeni özellik yok kararı sürüyor, ağa genişleme görüşmelerden sonra.

## Faz 6, W12: ortak veritabanı yardımcıları ve belge sadeleştirme

- **Ortak yardımcılar (PR #14):** `Infrastructure/Db/` altında `DbParams` (`P`, `V`, `PB`, `J`, `Cmd`, `ExecAsync`), `Outbox` ve `OutboxTopics`, `AdvisoryLock` (`etiket|uçuş` anahtarı tek yerde) ve `ReaderExtensions` (boş olabilen sütunlar). Üç sınıftaki özel kopyalar, altı outbox yazımı, üç bavul kilidi ve ~30 `IsDBNull ? null : …` satırı bunlara taşındı. Davranış aynı; tek fark: `ExitDeadlineService` komutları artık iptal belirtecini iletiyor (önceden yok sayılıyordu).
- **Belgeler:** `IMPLEMENTATION_STATUS.md` kısa ve güncel bir durum belgesine indirildi; paket anlatıları bu dosyaya taşındı. `REMEDIATION_PLAN.md` durum tablosu güncellendi.
- **Testler:** `DbHelpersTests` parametre tiplerini, kilit anahtarı biçimini ve outbox konu adlarını sabitler. SQL tarafı mevcut entegrasyon testleriyle doğrulandı (411 test, hepsi geçti).

## Faz 6, W10a: aktarma bavulu (veri ve akış)

- **Neden:** SITA 2026 Baggage IT Insights'a göre 2025'te yanlış yönetilen bavulların %39'u aktarma kaynaklı (bir önceki yıl %41). Antalya ağırlıklı olarak doğrudan uçuş alır; model bağlantılı uçuşları olan her havayolu ve hub için aynıdır.
- **Veri modeli:** aktarma bavulu iki bağlı satırdır; her satır yine (etiket, uçuş) ile tanınır, W12a'nın `etiket@uçuş` güvenliği aynen geçerlidir.
  - Gelen uçuştaki satır: `onward_flight_id` bağlantı uçuşunu gösterir. Bavul aktarma hattından geçince (ya da doğrudan yüklenince) `TRANSFERRED` ile kapanır (SYSTEM olayı, bir kez).
  - Bağlantı uçuşundaki satır: `inbound_flight_id` gelen uçuşu gösterir. Aktarma BSM'siyle açılır, `TRANSFER_SORTED` ve `LOADED_ON_AIRCRAFT` adımlarından geçer. Aktarma bavulu çoğu zaman yeniden taranmaz: güvenlik okuması kabul edilir ama atlanması sıra dışı sayılmaz. Aktarma hattının okuması okuma kapsamı profillerinde yer almaz; atlanması da sıra dışı sayılmaz.
  - Göç `0006_transfer_bags`: iki bağ sütunu (kendi uçuşunu gösteremez), kısmi indeksler, `flight_milestones.scheduled_departure_utc`.
- **Mesajlar:** BSM'de isteğe bağlı `.I/` (gelen uçuş) ve `.O/` (bağlantı uçuşu) satırları; kendi uçuşunu ya da ikisi aynı uçuşu gösteremez. Yeni okuma noktası `TRANSFER` (aktarma hattı). Gerçek IATA karşılıklarına benzer adlar, uyumluluk iddiası yok.
- **Bağın kurulması:**
  - Her satırın bağı kendi BSM'sinden gelir; geç gelen bir BSM eksik bağı tamamlar, kayıtlı bağı değiştirmez.
  - Aktarma BSM'si kaybolursa bağlantı satırının gelen uçuşu, gelen satırdaki `.O` bağından öğrenilir.
  - Kilitlenmeyi önlemek için gelen satıra yalnızca bağlantı satırını işleyen işlem dokunur ve kilitleri hep aynı sırada alır (önce bağlantı, sonra gelen satır).
- **Uçuş planı:** bağlantı uçuşunun planlanan kalkışı `POST /v1/flights/milestones` ile gelir (`scheduledDepartureUtc`; son bildirilen geçerlidir). Bavul kabul kapanışı planlanan kalkıştan `Transfer:CutoffMinutes` önce (varsayılan 15 dk, demo varsayımı).
- **Yolcu ekranı:** aktarma bavulu tek kart: ilk uçuş ve bağlantı uçuşu, adımlar "Teslim edildi, Güvenlik, Uçakta, İndirildi, Aktarmada, Bağlantı uçağında" (TR/EN). Bant, varış tahmini ve varış bildirimleri gösterilmez: bavul burada banda gelmez ve W4 metinleri yolcuyu bant başına yönlendirir. Yolcu jetonu iki satırın anahtarını taşır; canlı kanal iki uçuşun okumalarını da karta getirir. Bağlantı ertesi güne düşerse o bağlantının gelen satırı da aynı seyahat sayılır. Son adımda yolcuya bavulunu nerede alacağı söylenir ("varış havalimanında (ADB) bant başından alacaksınız"); varış havalimanı bağlantı uçuşunun mesajlarındaki .F satırından gelir ve uçuş satırına ilk mesajla yazılır.
- **Operasyon panosu:** gelen uçuş tablosunda "Aktarma" sütunu; aktarma bavulları banda gelmediği için varış sütunlarına girmez, toplama girer. Bağlantı uçuşları bu tabloda ve SLA'da yer almaz. Yeni "Aktarma bavulları" bölümü (`GET /v1/transfers`, yalnızca personel): bağlantı uçuşu başına planlanan kalkış, kabul kapanışı ve kalan süre, gelmedi, ayrıştırma bekliyor, aktarmada, yüklendi. Kapanış geçtiği halde yüklenmemiş bavullar "yetişmedi" diye vurgulanır.
- **Diğer kurallar:**
  - Kayıp bavul uyarıları bağlantı satırına uygulanmaz (kaçırılan bağlantı W10b'nin kuralıdır).
  - Gelen uçuşun ML geri bildirim eşiği banda gelecek bavullara göre hesaplanır.
  - Saklama süresinde `TRANSFERRED` ve bağlantı uçağına yüklenmiş bağlantı satırı teslim sayılır.
  - Durum sırası artık sayısal enum değerinden değil `BagStateMachine.Rank`'ten okunur (uyarı kuralları ve yolcu sorgusu dahil); yeni durumlar araya girebilir.
- **Simülatör:** canlı modda bavulların %6'sı aktarma (`--transfer-rate`; ölçüm modlarında ve CSV'de 0, ML eğitim verisi değişmez). Aktarma seçimi ayrı rastgele akıştadır; diğer bavulların mesajları aynen kalır. Bağlantı uçuşları 20 dk'lık kalkış tarifesinde (ZZ5xxx); bağlantı süresi inişten 40-150 dk sonra, aktarma hattı süresi indirmeden sonra 5 dk + log-normal (ortalama 10 dk, rejim B'de daha uzun), yükleme 3-8 dk sonra. Kabul kapanışına yetişmeyen bavulun bağlantı yüklemesi olmaz. Hepsi demo varsayımıdır.
- **Kaçırılan bağlantı:** bağlantı uçağı kalktıysa (AODB kalkış kilometre taşı) ve bavul yüklenmediyse yolcu kartı "bağlantı uçağınıza yetişmedi; varışta bant başında beklemek yerine bagaj hizmetlerine başvurun" der. Bu olmuş bir olaydır, yolcuya bu yüzden gösterilir. Kabul kapanışının geçmesi (henüz kalkış yok) yalnızca personel panosunda "yetişmedi" olarak görünür.
- **Bilinen sınırlar:** bağlantıyı kaçırma riski önceden tahmin edilmez ve uyarı tablosuna yazılmaz; panodaki "yetişmedi" türetilmiş bir sayıdır (W10b, sonraya bırakıldı). Takip aktarma havalimanında, bağlantı uçağına yüklemede biter: bağlantı uçuşunun varış tarafı (indirme, bant, alındı) başka bir havalimanının okuyucularındadır ve bu kurulumda görülmez. Havayolları bu veriyi birbirleriyle paylaştığı için bir havayolu kurulumunda izlenebilir (IATA Resolution 753'ün dört noktasından biri "varışta yolcuya teslim"dir); bu, varış havalimanından paylaşılan veri varsayımıyla ileride eklenebilir (REMEDIATION_PLAN, W10).
- **Testler:** durum makinesi (aktarma zinciri, devir, kapandıktan sonra yok sayma), mesaj alanları (geçerli ve geçersiz `.I/.O`), simülatör (oran 0'da değişiklik yok, diğer bavulların mesajları aynı, tarife ızgarası, kapanışa yetişmeyenler), gerçek PostgreSQL ile akış ve bağlar, kayıp aktarma BSM'si, uyarı dışlama, yolcu kartı ve iki anahtarlı jeton, pano sayaçları ve `/v1/transfers`, saklama süresi.

## Faz 6, W9: İngilizce yolcu ekranı

- **Kapsam:** yolcu sayfasının bütün metinleri Türkçe ve İngilizce: form, hata mesajları, adım çubuğu, durum cümleleri, kayıp bavul bildirimleri, varış tahmini ve tahminin dayanağı, süre ve saat biçimi, sayfa başlığı. Personel ekranları (operasyon panosu, uyarılar, SLA, güvenlik) şimdilik yalnızca Türkçe; dil seçici yalnızca yolcu ekranında görünür.
- **Yapı:** metinler tek bir sözlükte (`src/aerobag-ui/src/i18n.tsx`). İngilizce sözlük Türkçe sözlükle aynı tipi taşır: bir anahtar eksik ya da fazla olursa TypeScript derlemesi hata verir. `states.ts` artık yalnızca adım sırası ve kuralları tutar.
- **Varsayılan dil:** kullanıcının daha önce seçtiği dil (tarayıcıda `localStorage`), yoksa tarayıcının ilk dili Türkçe ise Türkçe, değilse İngilizce. Antalya'daki yabancı yolcu için ortak dil İngilizce kabul edildi; Almanca ve Rusça sözlük aynı yapıya eklenebilir.
- **Erişilebilirlik:** `<html lang>` seçilen dile göre değişir (ekran okuyucu doğru telaffuz eder). Dil düğmeleri `aria-pressed` taşır ve dil adını kendi dilinde okutur.
- **Hata mesajları:** metin değil türü saklanır; açık bir hata mesajı dil değiştirilince yeni dilde görünür.
- **Kontrol:** sözlüğün iki dilinde aynı 61 anahtar var; İngilizce metinlerde Türkçe karakter kalmadı (bir defalık betikle sınandı; arayüzde test çalıştırıcısı yok).

## Faz 6, W12a: yolcu jetonu uçuşa bağlı

- **Açık:** yolcu jetonu bavulu yalnızca etiket numarasıyla tanıyordu. Etiket numaraları zamanla yeniden kullanılır (şemada da `UNIQUE (bag_tag_number, flight_id)`). Uçuş belirtilmeyen `/v1/bags/{etiket}` en son güncellenen satırı döndürdüğü için, aynı etiket sonradan başka bir yolcunun bavuluna verildiğinde eski yolcu jetonun süresi içinde o bavulun durumunu görebiliyordu; canlı kanalda da onun bildirimlerini alıyordu.
- **Düzeltme:**
  - Jeton her bavulu `etiket@uçuş` olarak taşır (örn. `0891000000@ZZ0100-20261030`).
  - Yolcu için `/v1/bags/{etiket}` uçuşu jetondan alır. Başka bir uçuş istenirse 404 döner (varlığı sızdırılmaz). Personel için davranış aynı.
  - Canlı kanal grupları `bag:etiket@uçuş`. `JoinBags` bu biçimde anahtar ister; eski biçim (yalnız etiket) ve bozuk anahtarlar reddedilir. Uçuşu olmayan bir outbox yükü hiçbir bavul grubuna gitmez.
  - Yolcu sayfası canlı kanala bu anahtarlarla katılır. Eski biçimdeki bir yolcu jetonu yeni sürümde canlı kanala katılamaz ve bavul uçlarında 404 alır; yolcunun yeniden sorgu yapması yeter (jeton 30 dk geçerli).
- **Testler:** aynı etiket 22 ve 23 Ekim'de iki ayrı yolcuda; A yolcusu uçuş belirtmeden kendi bavulunu görür, B'ninkini ne REST'te ne canlı kanalda görebilir, personel ikisini de görür. Anahtar biçimi ayrıca birim testiyle sınanır (eksik hane, küçük harf, satır sonu, ASCII olmayan rakam, uzunluk sınırı).
- **Aynı PR'da, canlı denemede bulunan: aralıklı "ML yanıt vermedi" uyarısı.**
  - **Belirti:** ML servisi her isteğe 200 dönerken API arada bir zaman aşımı bildiriyordu.
  - **Neden:** API'nin varsayılan ML adresi `http://localhost:8000` idi; docker-compose servisi yalnızca IPv4'te (`127.0.0.1:8000`) yayınlar. Windows `localhost`'u önce `::1` (IPv6) ile dener ve orada dinleyen olmadığını ~2 sn sonra anlar. Bağlantı havuzundaki bağlantı boşta kalıp kapanınca her yeni bağlantı bu gecikmeyi ödüyor, 2 sn'lik zaman aşımına takılıyordu.
  - **Ölçüm (Windows, PowerShell `Invoke-RestMethod /health`):** `localhost` 2.167 ms, `127.0.0.1` 26 ms.
  - **Düzeltme:** varsayılan adres `http://127.0.0.1:8000`. Uyarı çıkmayan durumlarda da yeni bağlantı kuran yolcu sorguları ~2 sn bekliyordu; bu da kalktı.

## Faz 6, W7: yük testi

- **Araç:** `--mode load`. İmzalı Type B mesajlarını API'ye eşzamanlı gönderir.
  - Her bavulun yedi mesajı sırayla gider, eşzamanlılık bavullar arasındadır.
  - Rastgele etiket ve uçuş numaraları kullanılır; tohuma zaman katılır, her çalıştırma yeni bavul üretir.
  - Isınma koşusu sonuca katılmaz.
  - Eşzamanlılık düzeyi başına saniyedeki mesaj, gecikme yüzdelikleri (p50/p95/p99) ve yanıt kodları raporlanır; 10 sn'de bir ara rapor verilir.
- **Sonuç** (`EVALUATION.md`, "Yük testi"; i5-13500H, Docker'da PostgreSQL):
  - tek bağlantıda 149 mesaj/sn;
  - 64 eşzamanlılıkta 1.841 mesaj/sn (saatte ~6,6 milyon), p95 59 ms;
  - hata yok.
  - Yoğun bir havalimanının yoğun saati için hesaplanan hedef ~17 mesaj/sn.
- **Ölçümle bulunan ve düzeltilen:**
  - **Nonce önbelleği:** 50 bin kaydı aşınca her istekte bütün önbelleği dolaşıyordu. Temizlik artık en fazla 30 sn'de bir, tek iş parçacığında yapılıyor. Uzun koşudaki %29'luk yavaşlama kalktı.
  - **Operasyon saati sorgusu:** tabloyu baştan sona tarıyordu (~183 bin olayda 23,7 ms, tabloyla doğrusal büyür). Göç `0005_event_clock_index` kısmi indeks ekler; sorgu artık 0,1 ms sürüyor.
- **Darboğaz:** PostgreSQL'in işlemcisi (yük altında ~6,6 çekirdek). Disk yazması değil; `synchronous_commit = off` yalnızca %6 kazandırdı. Sonraki adımlar (otomatik hazırlanmış sorgular, mesaj başına daha az sorgu, toplu işleme) ölçülmedi.
- **Testler:** yük testinin ürettiği mesajlar gerçek ayrıştırıcıdan geçiyor ve sırayla yaşam döngüsünü taşıyor; etiketler çakışmıyor.

## Faz 6, W6: ML sözleşme testi ve görünürlük

- **Sorun:** API ile Python ML servisi arasındaki istek ve yanıt biçimi iki ayrı kod tabanında, elle tutarlı tutuluyordu. Python'da bir alan adı değişirse .NET tarafı sessizce taban çizgisine düşerdi. `MlHttpPredictor`'ın hiç testi yoktu. 28 Ekim canlı denemesinde tahmin indirme aşamasında HISTORY'ye düştü; neden düştüğü hiçbir yerde görünmüyordu.
- **Sözleşme:** `contracts/ml/` altında her uç için örnek istek ve yanıt (`predict`, `predict-bag`, `outcome`, `outcome-bags`, `health`). Sözleşme alan adları ve JSON tipleridir, değerler değildir.
  - **Python** (`ml/tests/test_contract.py`): her örnek istek servise gönderilir; servis kabul etmeli ve yanıtı örnekle aynı biçimde vermeli. Her sözleşme dosyasının kullanıldığı da denetlenir.
  - **.NET** (`MlContractTests`): `MlHttpPredictor` sahte bir HTTP işleyiciye karşı çalışır; gönderdiği istek örnekle aynı biçimde olmalı ve örnek yanıtı doğru okumalı.
  - Bir alan değişirse iki taraftan biri kırmızıya döner.
- **Görünürlük:**
  - `MlStatus` her çağrının sonucunu tutar: istek ve hata sayısı, son başarı ve son hata zamanı, gerekçe (HTTP kodu, zaman aşımı, bağlantı hatası, sözleşmeye uymayan yanıt) ve bekleme süresinin bitişi.
  - Hata durumuna geçildiği an (her denemede değil) uyarı loglanır; servis toparlanınca bilgi logu yazılır.
  - `GET /v1/ml/status` (personel). Operasyon panosunda bir durum satırı: "ML servisi yanıt veriyor", ya da gerekçe ve son hata zamanıyla "ML servisi yanıt vermiyor".
  - Zaman aşımı yapılandırılabilir: `Ml:TimeoutSeconds`, varsayılan 2 sn.
- **Bilinen sınır:** durum, API'nin son ML çağrısının sonucudur; servis ayrıca yoklanmaz. Trafik yokken (ör. simülasyon bitmişken) servis kapansa da satır "yanıt veriyor" kalır. Periyodik `/health` yoklaması sonraki bir iştir.
- **Davranış değişiklikleri:**
  - Geri bildirim isteklerinin (sonuç bildirimi) hatası da kaydedilir ama tahminleri bekletmez.
  - İstek iptal edilirse (uygulama kapanırken) hata sayılmaz.
- **Testler:**
  - **.NET:**
    - dört uç sözleşmeye uyuyor;
    - sunucu hatasında bekleme süresi, gerekçe ve toparlanma;
    - zaman aşımı gerekçesi;
    - sözleşmeye uymayan yanıta güvenilmiyor;
    - kısa kesintide son uçuş tahmini kullanılıyor;
    - geri bildirim hatası tahminleri bekletmiyor;
    - sözleşme dosyalarının hepsi mevcut;
    - `/v1/ml/status` yetkisi.
  - **Python:**
    - dört uç ve `/health` sözleşmeye uyuyor;
    - her sözleşme dosyası kullanılıyor;
    - biçim karşılaştırması tam sayı ile ondalığı aynı, mantıksalı farklı sayıyor.

## Faz 6, W5b: okuma kapsamına duyarlılık ölçümü

- **Ölçüm:** `--mode coverage-eval` (varsayılan 200 uçuş). Aynı gerçek zaman çizelgesi `full`, `typical` ve `minimal` profillerinde görülür. Kurallar API'dekilerle aynıdır: durum makinesi, kapsamaya göre sıra dışı, fizik penceresi, varsayılan ETA aralığı ve SLA'nın AODB'ye düşmesi. Raporladıkları:
  - bavul banda geldikten sonra yolcuya "bantta / büyük olasılıkla bantta" denene kadar geçen süre;
  - bavul gelmeden söylenen ve hiç söylenmeyen bilgi;
  - SLA'da ilk/son bavulun kaynağı (okuma ya da AODB) ve sapması;
  - kapsamı bilen ve bilmeyen kurala göre "sıra dışı" sayısı.
- **Kayıp bavul uyarısı:** `--mode alert-eval --coverage typical|minimal`.
- **Sonuçlar** (`EVALUATION.md`, "Okuma kapsamına duyarlılık"):
  - **`typical`:** yolcu bavul banda geldikten ~18 sn sonra bilgilendiriliyor; SLA 200 uçuşun hepsinde AODB'den; kayıp bavul uyarısı `full` ile aynı.
  - **`minimal`:** yolcu ~8 dk geç öğreniyor; indirme okuması kaybolanlara hiçbir şey söylenemiyor.
  - **"Sıra dışı":** kapsamı bilmeyen kural `typical`'da 1000 bavulda 1058, `minimal`'de 2000 sayardı.
- **Ölçüme göre değişen:** indirmeden sonra ikinci bir okuyucusu olmayan kurulumda (`ReadCoverage.HasReadAfterUnload` yanlış) MISSING_AT_ARRIVAL yalnızca personele gösteriliyor. Ölçümde `minimal` profilde yolcuya giden MISSING uyarılarının ~9'da 8'i kaybolan indirme okumasından doğan, hiç kapanmayan yanlış uyarıydı.
- **Testler:**
  - tam kapsamda kayıpsız gecikme sıfır;
  - tipik kapsamda bilgi fizik payı içinde ve SLA AODB'den;
  - kapsamı bilmek yanlış "sıra dışı"ları kaldırıyor, okuyucusu olan noktadaki kayıp hâlâ sayılıyor;
  - en az kapsamda varsayılan aralık;
  - en az kapsamda kaybolan indirme okuması yolcuya yanlış MISSING olarak ulaşmıyor;
  - kural düzeyinde MISSING'in yalnızca personele gösterilmesi.

## Faz 6, W5 (ilk dilim): okuma kapsamı profilleri

- **Sorun:** sistem her noktada (güvenlik, yükleme, indirme, şut, bant ağzı, banttan alma) okuyucu olduğunu varsayıyordu. Çoğu havalimanında bant ağzında ve banttan almada okuyucu yoktur. Bu varsayım bozulunca şunlar oluyordu:
  - her bavul "sıra dışı" sayılırdı;
  - ExitGuard her bavulda "banda gelmeden çıkışta okundu" yanlış alarmı verirdi;
  - SLA'da son bavul hiç gerçekleşmezdi;
  - yolcu tahmin süresi geçtikten sonra da "1 dakikadan az" görürdü;
  - teslim durumuna ulaşmayan bavulların yolcularının arama özetleri **hiç silinmezdi** (KVKK).
- **Model:** `Application/Coverage/ReadCoverage`. Okuyucu yerleşimi altyapının bir özelliğidir; uçuştan uçuşa tahmin edilmez, yapılandırmayla verilir (`Coverage:Profile`, geçersiz değerde API açılmaz). Profiller:
  - `full`: her noktada okuyucu (varsayılan, simülatörün eski davranışı);
  - `typical`: güvenlik, yükleme, indirme ve şut; bant ağzı ve banttan alma yok;
  - `minimal`: yalnızca yükleme ve indirme (BSM her zaman vardır).
- **Kapsamaya uyan davranışlar:**
  - **Sıra dışı:** atlanan adım yalnızca o noktada okuyucu varsa ANOMALY sayılır. Durum işlemcisi ve çıkış kapısı aynı kuralı kullanır.
  - **ExitGuard:** "sıra dışı çıkış" eşiği, çıkıştan önce okunabilen en ileri varış durumudur (bant ağzı, yoksa şut, yoksa indirme). Bant ağzı olmayan kurulumda gerekçe `NOT_ARRIVED` olur.
  - **SLA:** gerçekleşen ilk ve son bavul, bant okumaları yoksa yer hizmetinin AODB'ye bildirdiği uçuş düzeyi zamanlardan gelir. Göç `0004_belt_milestones` `flight_milestones`'a `first_bag_on_belt_utc` ve `last_bag_on_belt_utc` ekler. Kaynak yanıtta yazar (`timesSource`: READS, AODB, NONE); panoda AODB ise not düşülür. AODB "son bavul bantta" dediyse kayıp bavul son bavulu bekletmez.
  - **Operasyon panosu:** ilk/son bavul zamanı AODB'den gelir. Bant okuması yoksa "takılı" bavul sayılamaz (`null`, bilinmiyor).
  - **Yolcu kartı:** bant ağzı okuyucusu yoksa ve tahmin aralığı geçtiyse "Bavulunuz büyük olasılıkla bantta" (`LIKELY_ON_BELT`) gösterilir. Bu türetilmiş bir bilgidir, durum değişmez. Tam kapsamda gösterilmez, çünkü orada bant okuması gelecektir.
  - **Çıkış süresi:** banttan alma okumasıyla başladığı için o okuma yoksa hiç başlamaz. Çıkış bilgisi yalnızca ExitGuard kapı okumasıdır.
  - **Saklama süresi (KVKK):** teslim durumuna hiç ulaşmayan bavulların yolcuları için son güncellemeden 90 gün üst sınır (`Retention:UndeliveredDays`).
- **Simülatör:**
  - `--coverage full|typical|minimal`: okuyucusu olmayan noktanın mesajı hiç üretilmez, gerçek zamanlar değişmez.
  - Yer hizmetinin ilk/son bavul bantta bildirimi, kilometre taşı olarak olay anında gönderilir.
  - Canlı modda API'nin de aynı profille açılması gerektiği hatırlatılır.
- **Bilinen sınırlar:**
  - Bant ağzı okuması olmayan kurulumda bavul düzeyi ML geri bildirimi ve ampirik ETA istatistiği çalışmaz (bant zamanı yok). ETA, indirme sonrası ML ya da varsayılan aralığa ve şutta fiziğe dayanır.
  - Uçuş düzeyi ML geri bildirimi AODB zamanlarını henüz kullanmıyor.
  - Panodaki "Bantta" sayacı bu kurulumda sıfır kalır.
- **Testler:**
  - **Birim:**
    - profil ayrıştırma;
    - sıra dışı kuralı;
    - en ileri varış durumu;
    - ExitGuard'ın şuttaki bavula karar vermesi;
    - simülatörün profil başına yalnızca kapsamdaki okumaları üretmesi;
    - kapsamın gerçek zamanları değiştirmemesi;
    - bant kilometre taşları ve doğrulaması.
  - **Entegrasyon:**
    - okuyucusu olmayan noktanın atlanması APPLIED, olanınki ANOMALY;
    - tipik kapsamlı API'de çıkış kapısı şuttaki bavulu sıra dışı saymadan geçiriyor;
    - SLA ve pano AODB zamanlarına düşüyor, "takılı" bilinmiyor;
    - veri yokken kaynak NONE;
    - yolcu, aralık geçince "büyük olasılıkla bantta" görüyor (tam kapsamda görmüyor);
    - tutarsız bant zamanı 400 alıyor;
    - teslim edilmeyen bavulun yolcusu 90 gün sonra anonimleşiyor.

## Faz 6, W4c: kayıp bavul senaryoları ve ölçüm

- **Senaryolar (simülatör):** `--not-loaded-rate` (varsayılan %0,3) ve `--missing-rate` (varsayılan %0,2), canlı modda ve ölçümde açık.
  - Yüklenmeyen bavulun BSM ve güvenlik okuması olur, sonrası olmaz. Varışta bulunmayan bavul yüklenir, sonra iz kaybolur.
  - Toplam %0,5, kamuya açık oranlarla aynı mertebede: ABD Ulaştırma Bakanlığı 2024 için her 100 bavulda 0,55 yanlış işlenen bavul bildiriyor. SITA'ya göre yanlış işlenen bavulların %16'sı yükleme hatasından. Simülatörde aktarma yok.
  - Senaryolar ayrı bir rastgele akış kullanır; diğer bavulların verisi değişmez. CSV (ML eğitimi) senaryo bavullarını içermez ve CSV modunda oranlar varsayılan olarak sıfırdır.
  - İlk ve son bavul ile "indirme bitti" zamanı yalnızca gerçekten indirilen bavullardan hesaplanır.
  - Canlı modda senaryo bavullarının PNR ve soyadı yazdırılır; yolcu sayfasında uyarıyı görmek için kullanılabilir.
- **Kilometre taşları olay anında:** kalkış ve "indirme bitti" artık bavul okumalarıyla aynı zaman çizelgesinde, olay anında gönderilir (`MILESTONE` türü). AODB beslemesi güvenilir kabul edilir: iletim gürültüsü yalnızca bavul okumalarına uygulanır.
- **Ölçüm:** `--mode alert-eval` (varsayılan 200 uçuş). Kural API'dekinin aynısıdır (`BagAlertRules`).
  - Okuma kaybı canlı göndericideki gibi modellenir (düşme, sonra bozulma). Bozuk mesaj ayrıştırılamaz, kayıp sayılır.
  - Bavulun durumu kendisine ulaşan okumaların en ilerisidir; API'deki durum makinesinin davranışı budur.
  - Uyarı işçisinin gecikmesi (≤5 sn) sıfır kabul edilir.
  - Ölçütler:
    - yakalama oranı: yüklenmeyende inişten önce; varışta bulunmayanda hem genel hem bant boşalmadan önce;
    - tespit gecikmesi;
    - okuma kaybından doğan yanlış uyarı (1000 bavulda);
    - yanlış uyarının ne kadar süre açık kaldığı.
  - Sonuçlar `EVALUATION.md`'dedir ("Kayıp bavul uyarısı"). Özet: yüklenmeyen bavulların hepsi inişten önce personel ekranına düşüyor, ama bu uyarıların ~5'te 4'ü kaçırılan yükleme okumasından doğan yanlış uyarı ve varışa kadar açık kalıyordu.
  - **Bu ölçüme göre değişenler:** varış toleransı 10 dk'dan 5 dk'ya indi; NOT_LOADED yolcuya yalnızca varışta, "indirme bitti" toleransı geçince gösteriliyor.
  - **Sonuç:** yolcuya giden yanlış uyarı 1000 bavulda 15,5'ten 1,8'e düştü; gerçek vakaların %89'unda yolcu bant boşalmadan haberdar oluyor.
- **Bilinen sınır:** NOT_LOADED yolcuya görünür hale geldiğinde ayrıca canlı bildirim gitmiyor; yolcu sayfası en geç 20 saniyede (canlı bağlantıda) ya da 3 saniyede kendini yeniliyor.
- **Testler:**
  - senaryolar normal bavulların verisini değiştirmiyor;
  - senaryo bavullarının mesajları doğru yerde kesiliyor;
  - ilk/son bavul ve "indirme bitti" yalnızca indirilen bavullardan;
  - kilometre taşları doğru anda planlanıyor;
  - kayıpsız durumda her senaryo bavulu tam tolerans süresinde yakalanıyor ve hiç yanlış uyarı yok;
  - okuma kaybı yanlış uyarı üretiyor ve çoğu sonraki okumayla kapanıyor;
  - aynı tohum aynı ölçümü veriyor;
  - uyarı geçmişi tolerans sınırında açılıp sonraki okumada kapanıyor.

## Faz 6, W4 (ilk dilim): kayıp ve geciken bavul tespiti, ekranlar

- **Sorun:** sistem bavulun nerede olduğunu söylüyordu ama gelmeyeceğini söylemiyordu. Uçağa yüklenmeyen ya da varışta indirilmeyen bavulun yolcusu bant başında boşuna bekliyordu. 28 Ekim canlı denemesinde de indirmesi geç başlayan bir uçuşta (ZZ0102) yolcu hiçbir bilgi görmedi.
- **Model:** kayıp bavul bir **durum değil, uyarıdır**. Durum makinesi monoton kalır; bavul sonradan okunursa uyarı "bulundu" diye kapanır. Bir bavulda en fazla bir uyarı geçerlidir:
  - `NOT_LOADED`: uçak kalktı (kalkıştan sonra tolerans süresi geçti) ve bavulun yükleme okuması yok. Varışta da sürer.
  - `MISSING_AT_ARRIVAL`: varışta "indirme bitti" (tolerans süresi geçti) ve bavul indirilmedi. Kalkış zamanı bilinmiyorsa yükleme okuması olmayan bavul da buraya düşer.
  - `ARRIVAL_DELAYED` saklanmaz, sorguda türetilir: on-block'tan sonra ilk bavul hedefi (SLA, varsayılan 15 dk) geçti ve uçuştan henüz hiç bavul inmedi.
- **Kurallar** saf ve birim test edilir (`Application/Alerts/BagAlertRules`). Tolerans süreleri yapılandırılır (`Alerts:NotLoadedGraceMinutes` varsayılan 10 dk, `Alerts:MissingGraceMinutes` varsayılan 5 dk). Varış toleransı W4c ölçümüyle seçildi; saha verisiyle yeniden seçilmelidir.
- **Veri modeli:** göç `0003_bag_alerts`:
  - `flight_milestones` (kalkış, indirme bitti). Plandan sapma: alanlar `flight_context`'e değil ayrı tabloya kondu. Bunlar zaman içinde gelen olaylardır, ML'in sabit özellikleri değildir; bağlamdan önce de gelebilirler.
  - `bag_alerts`: tür, açılış, kapanış, gerekçe (`FOUND`, `OFFLOADED`, `CLEARED`). Bir bavulda aynı türden tek açık uyarı (kısmi tekil indeks). Uygulama rolü uyarı silemez; kapanış tek kez yazılır (tetikleyici); güncelleme yetkisi yalnızca kapanış sütunlarında.
  - `bags(flight_id)` indeksi.
- **Akış:**
  - `POST /v1/flights/milestones` (imzalı, kapsam `context`); verilmeyen alan kayıtlı değeri korur.
  - `BagAlertBackgroundService` 5 sn'de bir, operasyon saatiyle (W2) kuralı uygular ve farkı yazar. Tek örnek çalışır (danışma kilidi).
  - Her açılış ve kapanış için içeriksiz canlı bildirim yazılır (outbox `bag.alerts`).
- **API ve arayüz:**
  - **Yolcu yanıtı:** her bavul için `notice` alanı. Açık uyarıda ETA verilmez; işçi henüz kapatmamış ama bavul okunmuşsa eski uyarı gösterilmez. **`NOT_LOADED` yolcuya yalnızca varışta, "indirme bitti" toleransı geçince gösterilir** (W4c ölçümünden sonra; personel kalkıştan sonra görür).
  - **Yolcu kartı:** sakin dilde bir kutu, "bu uçağa yüklendiğine dair kayıt yok" ya da "indirme tamamlandı, bavulunuz okunmadı". Uyarıda bant numarası gizlenir. Gecikmede "indirme beklenenden geç başladı" yazar.
  - **Operasyon panosu:** "Eksik" sütunu ve "Eksik bavullar" listesi (`GET /v1/alerts`, personel).
- **Simülatör:** kalkış ve indirme bitti zamanlarını gönderir (W4c'den beri olay anında, bkz. aşağı).
- **Ek düzeltme (canlı denemede bulundu):** aynı PNR ve soyad iki günün simülasyonunda da vardı, yolcu sorgusu iki seyahati birden döndürüyordu. PNR'lar gerçekte de zamanla yeniden kullanılır. Artık yalnızca en son uçuş tarihindeki bavullar döner; böylece PNR'ı bilen biri eski bir seyahati göremez (veri en aza indirme).
- **Bilinen sınırlar:**
  - **Etiket numarası tekrarı:** yolcu jetonu bavulu yalnızca etiket numarasıyla tanır. Etiketler de zamanla yeniden kullanıldığından jetonun uçuşla birlikte kapsanması gerekir (W12'ye not edildi).
  - **Okuyucu kaçırması:** uyarı bir tespittir, hüküm değildir. Kaçırılan bir yükleme okuması (`--drop-rate`) yanlış bir `NOT_LOADED` uyarısı üretir ve bu uyarı varışta bavul okunana kadar açık kalır. Yolcu metni bu yüzden "kayıt bulunamadı" der. Oranı W4c ölçecek; azaltmak W5'in (okuma kapsamı) konusudur.
  - **Simülatör senaryoları:** yüklenmeyen ve kaybolan bavul senaryoları ile tespit oranı, yanlış uyarı ve tespit gecikmesi ölçümü W4c'dedir.
  - **Kapsam dışı:** WorldTracer/PIR entegrasyonu.
- **Testler:**
  - **Birim:** sınır süreler, tek uyarı kuralı, kapanış gerekçeleri, eski uyarının gizlenmesi, gecikme kuralı, kilometre taşı doğrulaması.
  - **Entegrasyon (uygulama rolüyle):**
    - Uyarı tolerans süresinden önce açılmıyor, sonra açılıyor.
    - Sonradan okunan bavulda uyarı `FOUND` ile kapanıyor; uçağa alınmayan bavulda `OFFLOADED` ile.
    - Tekrar taramada uyarı çoğalmıyor.
    - Hiç yüklenmeyen bavul varışta tek uyarıyla kalıyor.
    - Kapanmış uyarı değiştirilemiyor, uyarı silinemiyor.
    - Ops özeti ve listesi uyarıyı gösteriyor.
    - Yolcu yanıtında bildirim var, ETA yok.
    - Gecikme bildirimi ilk bavul inince kalkıyor.
    - Kilometre taşı ucu imza istiyor, değerleri birleştiriyor ve doğruluyor.

## Faz 6, W3: bant numarası

- **Sorun:** yolcu bavulunun nerede olduğunu görüyordu ama hangi banda gideceğini görmüyordu. Simülatör bandı biliyordu, göndermiyordu; şemadaki `flights.assigned_carousel` hiç kullanılmıyordu.
- **Veri modeli:** göç `0002_flight_carousel`: `flight_context.carousel` (1-8 büyük harf ya da rakam, veritabanında CHECK kısıtı); kullanılmayan `flights.assigned_carousel` kaldırıldı. Bandın tek kaynağı AODB bağlamıdır.
- **Akış:** `POST /v1/flights/context` gövdesinde isteğe bağlı `carousel`. Verilmezse kayıtlı bant korunur (bandı bilmeyen bir kaynak onu silmesin diye). Bant ilk kez atanır ya da değişirse aynı işlemde `flight.context` konulu bir outbox mesajı yazılır; yayıncı operasyon panosuna `flightsChanged`, o uçuştaki her bavulun grubuna `bagChanged` gönderir. Bildirim yine içeriksizdir: istemci yeni bandı `/v1/lookup` ile çeker.
- **API ve arayüz:** yolcu yanıtında her bavul için `carousel`; bavul kartında sarı "Bant N" etiketi (bavul banttan alınana kadar) ve bantta iken "Bagajınız N numaralı bantta". Operasyon tablosunda bant sütunu. Bant ML özelliği değildir, modele gönderilmez.
- **Simülatör:** her uçuşun bandını bağlamla gönderir (`carousel`).
- **Bilinen sınırlar:** simülatör bant değişikliği senaryosu üretmiyor (değişiklik testlerle doğrulanıyor). Bant, uçuş düzeyindedir; aynı uçuşun bavullarının farklı bantlara bölünmesi desteklenmiyor. BPM'deki `.Q` (hedef bant) alanı ayrıştırılıyor ama kullanılmıyor.
- **Testler (W3):** bant atanmadan önce yanıtta yok, atanınca var; bantta bekleyen bavulda da dönüyor; değişince yeni değer dönüyor ve uçuştaki tüm bavulları içeren tek bildirim yazılıyor; aynı bant ya da bantsız bağlam tekrar gelince bant korunuyor ve bildirim yazılmıyor; operasyon tablosunda görünüyor; uç nokta hatalı biçimi 400 ile reddediyor, veritabanı da CHECK ile; 0001'den 0002'ye yükseltmede mevcut bağlam satırları korunuyor.

## Faz 6, W2: operasyon saati

- **Sorun:** "şimdi" beş ayrı yerde (çıkış süresi işi, ML geri bildirimi, takılı bavul sayacı, yolcu ETA'sı, SLA) SQL içinde "en son olay zamanı" olarak hesaplanıyordu. Gerçek bir kurulumda bu yanlış sonuç verirdi, olay akışı durunca da saat dururdu.
- **Çözüm:** `IOperationalClock` (Application). İki uygulama: `SystemOperationalClock` (gerçek saat) ve `EventDerivedClock` (en son BSM olmayan olay zamanı, simülasyon). Seçim `Clock:Mode` ile: `event` (varsayılan, demo simülasyonla çalıştığı için) ya da `real`. Geçersiz değerde API açılmaz; `event` modunda açılışta uyarı yazılır.
- **Değişen yerler:** `ExitDeadlineService`, `FlightContextStore.PendingOutcomesAsync` (artık "şimdi"yi parametre alır), `OpsQueries`, `PassengerQueries`, `SlaQueries`, `MlOutcomeBackgroundService`. SQL'deki alt sorgular kalktı, saat parametre olarak verilir. Saat yoksa (henüz hiç olay yok) takılı bavul sayılmaz, çıkış süresi ve ML geri bildirimi beklemez.
- **Kapsam dışı:** saklama süresi (KVKK) her zaman gerçek saati kullanır; olay alınma zamanları ve jeton süreleri de gerçek saattir.
- **Testler:** kaynak kodunu tarayan bir test, "en son olay zamanı" ile şimdi hesaplayan tek dosyanın `EventDerivedClock.cs` olduğunu doğrular (W2'nin bitti ölçütü). Sabit saatle: takılı bavul eşiği, çıkış süresi, SLA'da geçen süre, ML geri bildiriminin 10 dakikalık bekleme süresi ve yolcu ETA'sının referans anı, verilen saate göre doğru sonuç verir.

## Faz 6, W1: şema göçü

- **Göç dosyaları:** `db/migrations/NNNN_ad.sql`; eski `db/init.sql` ilk göç oldu (`0001_init.sql`). Kurallar `db/migrations/README.md`'de.
- **Göç aracı:** `AeroBag.Migrator` göçleri tablo sahibi hesabıyla uygular. Aynı anda iki aracın çalışmasını danışma kilidiyle önler. Her göçü kendi işleminde uygular, sürümü aynı işlemde kaydeder; hata olursa hiçbir şey kalmaz. `--status` yalnızca listeler.
- **Değiştirilmezlik:** her göçün SHA-256 özeti `schema_migrations`'a yazılır. Uygulanmış bir dosya değişirse ya da veritabanı koddan yeniyse araç durur. Özet satır sonundan ve BOM'dan bağımsızdır (Windows ve Linux aynı özeti verir).
- **Eski veritabanı:** göç kaydı olmayan ama tabloları olan bir veritabanı (`init.sql` dönemi) tanınır; araç bir kez `docker compose down -v` yapılmasını söyler. Bu son sıfırlamadır.
- **API:** açılışta şema sürümünü okur (uygulama rolüne yalnızca `schema_migrations` üzerinde SELECT verildi). Sürüm kodunkiyle aynı değilse açılmaz ve göç aracını çalıştırmayı söyler. Veritabanına hiç ulaşılamazsa eskisi gibi uyarıyla açılır.
- **docker-compose:** şemayı artık kurmuyor; yalnızca boş veritabanı açıyor.
- **Testler:** göç listesi kuralları (ad biçimi, sıra, tekrar, boşluk, özet) birim testli. Gerçek PostgreSQL'de: boş veritabanına bütün göçler ve ikinci çalıştırmanın bir şey yapmaması, bekleyen göçlerin veriyi koruyarak uygulanması, değiştirilmiş göçte durma, satır sonu farkının değişiklik sayılmaması, hatalı göçün geri alınması, koddan yeni veritabanı, eski şema tespiti, uygulama rolünün sürümü okuyup DDL yapamaması. Bütün entegrasyon testlerinin fikstürü artık şemayı göç aracıyla kurar.

Bilinen sınırlar:
- Geri alma (down) göçü yok; geri dönüş yeni bir ileri göçle yapılır.
- İşlem içinde çalışamayan komutlar (`CREATE INDEX CONCURRENTLY` gibi) bu araçla uygulanamaz.
- Uygulama rolünün parolası ilk göçte yalnızca yerel demo için yazılıdır; gerçek kurulumda `ALTER ROLE` ile değiştirilir.

## Faz 4.5: tehdit modeli, KVKK notu ve saklama süresi

- **`docs/THREAT_MODEL.md`:** STRIDE tablosu; her tehdit için önlem, onu doğrulayan test ve açık kalanlar. Otomatik testi olmayan önlemler (hız sınırı, 413, canlı kanalın içeriksizliği, adres kayıtlarının kapatılması) açıkça "otomatik test yok" ya da "kod incelemesi" diye işaretlendi.
- **`docs/KVKK.md`:** hangi verinin nereye gittiği, uygulanan ilkeler, açık kalanlar. Hukuki danışmanlık değildir.
- **Bulunan ikinci açık:** yolcu sorgusu `GET /v1/lookup?pnr=...&surname=...` idi ve ASP.NET Core istek adresini sorgu parametreleriyle konsola yazıyordu; PNR ve soyad API kayıtlarına düşüyordu. Sorgu `POST /v1/lookup` (JSON gövde) oldu ve istek adresi kayıtları uyarı düzeyine çekildi (bu, canlı kanalın `access_token`'ını da kayıtlardan çıkarır).
- **Saklama süresi:** `RetentionService` saatte bir, bütün bavulları teslim edilmiş ve son güncellemesinden 30 gün geçmiş yolcuların `lookup_key` ve `pnr_key` değerlerini rastgele değerle değiştirir (`Retention:PersonalDataDays`). Uygulama rolü yolcu satırında yalnızca bu sütunları güncelleyebilir (sütun düzeyi yetki). Testler: süre dolunca yolcu bulunamaz, süre dolmadan ve teslim edilmemiş bavulda dokunulmaz, ikinci çalıştırma tekrar değiştirmez, uygulama rolü başka sütunu değiştiremez.

## Faz 4.4 (ikinci dilim): kapı paketleri, yanlış alarm ölçümü, güvenlik ekranı

- **Simülatör:** canlı modda çıkış okumaları artık varsayılan olarak ExitGuard kapı paketidir (`--exit-mode bpm` ile eski BPM EXIT). Sensör modeli `GateModel.cs`'tedir ve bütün oranlar demo varsayımıdır: bavulların %25'i bir öncekiyle aynı rezervasyonda, grup bavulunun %30'unu gruptaki başka biri çıkarır, %0,5'ini rezervasyonla ilgisi olmayan biri çıkarır, %2'si hiç okunmaz, %3'ünde doğrulama yoktur, okumaların %15'i zayıf sinyallidir (0,20-0,75), zayıf okumaların %30'unda PNR'ın bir karakteri yanlış okunur, %1'inde yan koridordaki bir bavul da pakete karışır. Bu modda yalnızca PNR'lar ve çıkış mesajları değişir; bavul zaman çizelgesi ve CSV/ML verisi aynıdır (birim testi).
- **Ölçüm:** `--mode gate-eval` API'siz, çok sayıda uçuşta (varsayılan 200) aynı karar kuralını (`ExitGuardRules`) çalıştırır ve farklı sinyal eşiklerinde yanlış alarm, kararsız okuma ve yakalanan uyuşmazlık sayılarını raporlar. Canlı modda simülatör, API'nin her etiket için verdiği kararı gerçek durumla birlikte sayar. Sonuçlar `EVALUATION.md`'dedir: varsayılan eşikte (0,60) yanlış alarm %1,38, kararsız okuma %13,5, başkasının yakalanması %87, başkasının sahip diye geçmesi 0. Açılan incelemelerin yalnızca yaklaşık %24'ü gerçek vaka (taban oran etkisi).
- **Arayüz:** güvenlik rolüyle girilen panoda "Çıkış inceleme kayıtları" bölümü (gerekçe, uçuş, kapı, zaman; "Yanlış alarm" ve "Doğrulandı" düğmeleri, isteğe bağlı not). Operasyon tablosunda "İncelemede" sütunu. Yolcu sayfası `EXIT_REVIEW` durumunda ARCHITECTURE.md 6.3'teki bilgilendirme mesajını gösterir (alarm dili yok).

Bilinen sınırlar:
- Yanlış alarm ve kaçırma oranları sensör varsayımlarının sonucudur; kural doğru çalışıyor mu sorusunu yanıtlar, sahadaki oranı vermez (Faz 5'te ölçülmesi gerekir).
- Yan koridordan okunan bavul, sahibi henüz çıkmadıysa `EXIT_REVIEW` olur; gerçek sahibi daha sonra geçtiğinde bavul zaten kapanmış sayılır. Bu bir yanlış alarmın gerçek maliyetidir ve inceleme kaydıyla personele bırakılır.

## Faz 4.4 (ilk dilim): ExitGuard arka ucu

- **Uç:** `POST /v1/gate/scans` (imzalı, kapsam `gate`). Paket ARCHITECTURE.md 4.3'teki gibidir: kapı, zaman, okunan etiketler (en çok 10), sunulan PNR, yöntem, sinyal kalitesi. Özellik bayrağı `ExitGuard:Enabled` (varsayılan açık); kapalıyken uç yoktur (404) ve eski çıkış okuması (BPM `EXIT`) aynen çalışır.
- **Kural (saf fonksiyon, `ExitGuardRules`):** sunulan PNR'ın özeti bavulun yolcusunun PNR özetiyle aynıysa `CLEARED_OWNER` (bavul `EXIT_CLEARED`). Eşleştirme PNR düzeyindedir: aynı rezervasyondaki yolcular birbirinin bavulunu alabilir, soyad karşılaştırılmaz. Uyuşmazsa `REVIEW_MISMATCH` (bavul `EXIT_REVIEW`, sessiz inceleme kaydı; kapıda alarm yok). Doğrulama yoksa, sinyal 0,6'nın altındaysa ya da bavulun sahip bilgisi yoksa `NO_DECISION`: durum değişmez, alarm üretilmez, çıkış süresi işlemeye devam eder. Bant öncesi bavul `OUT_OF_SEQUENCE`, sistemde olmayan etiket `UNKNOWN_TAG` (ikisi de inceleme kaydı, durum değişmez). Kapanmış bavul `ALREADY_CLOSED`.
- **Saklanan veri:** sunulan PNR ve soyad hiçbir tabloya yazılmaz; yalnızca bellekte HMAC özeti alınıp karşılaştırılır. Yolcu tablosunda PNR'ın ayrı bir HMAC özeti (`pnr_key`, arama anahtarından farklı önekle) tutulur. Her okuma kararıyla birlikte `gate_reads`'e (yalnızca eklemeli, denetim izi), durum değiştiren kararlar olay günlüğüne ve outbox'a yazılır. Aynı paket ikinci kez gelirse `DUPLICATE`.
- **Güvenlik ekranı uçları:** `GET /v1/security/reviews` (açık kayıtlar; `?status=all` tümü) ve `POST /v1/security/reviews/{id}/resolve` (`CONFIRMED` ya da `FALSE_ALARM`, not en çok 200 karakter). Yalnızca `security` rolü; `ops` rolü 403 alır. Kayıtta kişisel veri yoktur: etiket, uçuş, kapı, zaman, gerekçe, kimin sonuçlandırdığı.
- **Testler:** karar kuralı (birim); sahip, aynı rezervasyon, başkası, zayıf sinyal ve doğrulamasız okuma, bilinmeyen etiket, bant öncesi okuma, tekrar gelen paket, imza ve kapsam, geçersiz paket, bayrak kapalı, sunulan PNR'ın hiçbir tabloda bulunmaması, inceleme kaydını yalnızca güvenliğin görüp sonuçlandırabilmesi (entegrasyon).

Bilinen sınırlar:
- Kuralın kendisi deterministiktir; yanlış alarm ve kaçırma oranları sensörün okuma hatalarına bağlıdır. Bunlar sonraki dilimde simülatörle ölçülecek ve yalnızca varsayılan hata oranları altında geçerli olacak.
- PNR'ı paylaşan herkes (aynı rezervasyon) birbirinin bavulunu çıkarabilir; bu bilinçli bir kuraldır ve PNR'ı ele geçiren biri için açık bir yoldur.
- İnceleme kayıtları canlı kanaldan bildirilmiyor; güvenlik ekranı 5 sn'de bir yoklar.

## Faz 4.3: yalnızca eklemeli olay günlüğü ve ham metinde kişisel veri

- **Bulunan açık:** olay günlüğü (`baggage_events.raw_text`) ve ölü mektup tablosu, gelen BSM'nin ham metnini saklıyordu; bu metinde yolcu adı (`.P/`) ve PNR (`.L/`) düz metin olarak vardı. Bu, "ad ve PNR düz metin saklanmaz" iddiasıyla çelişiyordu.
- **Düzeltme:** ham metin saklanmadan önce `.P/` ve `.L/` satırları maskelenir (`TypeBRedactor`). Ölü mektup kaydında hata metninden de bu değerler temizlenir. Arama için gereken bilgi zaten HMAC özetindedir.
- **Uygulama rolü:** API artık `aerobag_app` rolüyle bağlanır. Bu rolün hiçbir tabloda DELETE yetkisi yoktur; olay günlüğünde yalnızca SELECT ve INSERT, ölü mektup tablosunda yalnızca INSERT (okuyamaz), şemada tablo oluşturamaz. Diğer tablolarda yalnızca kodun kullandığı işlemler açıktır.
- **Tetikleyici:** olay günlüğünde UPDATE, DELETE ve TRUNCATE, tablo sahibi için de tetikleyiciyle engellenir.
- **Testler:** uygulama rolüyle olay yazılabilir ama değiştirilemez ve silinemez; hiçbir tabloda silme yok; ölü mektuplar okunamaz; tablo sahibi de olay günlüğünü değiştiremez; ham metinde ve ölü mektupta ad ve PNR kalmaz. Ayrıca tüm API testleri artık kısıtlı rolle çalışır, yani yetkiler API'nin işi için yeterlidir.
- Bağlantı dizesi değişti: `Username=aerobag_app;Password=aerobag_app_dev` (yalnızca yerel demo).

Bilinen sınırlar:
- Süper kullanıcı tetikleyiciyi kapatabilir ya da tabloyu silebilir. Bu katman kazara ya da uygulama üzerinden yapılan değişikliği engeller; veritabanı yöneticisine karşı koruma değildir. Ona karşı olay zinciri özeti (hash chain) ya da harici, değiştirilemez bir kayıt deposu gerekir; yapılmadı.
- Daha önce oluşturulmuş veritabanlarındaki eski ham metinler bu değişiklikle temizlenmez; demo verisi olduğu için `docker compose down -v` ile sıfırlanır.
- Saklama süresi dolan kişisel verinin silinmesi (KVKK) için uygulama rolüne ileride yalnızca o işe özel bir yetki verilmesi gerekecek.

## Faz 4.2: mesaj kaynaklarının imzası

- **Uçlar:** `/v1/events` (kapsam `events`) ve `/v1/flights/context` (kapsam `context`) yalnızca imzalı istek kabul eder. Kapı sensörü için `gate` kapsamı ayrıldı.
- **İmza:** başlıklar `X-AeroBag-Source`, `X-AeroBag-Timestamp` (unix saniye), `X-AeroBag-Nonce`, `X-AeroBag-Signature`. İmzalanan metin `v1\n{zaman}\n{nonce}\n{METHOD}\n{yol}\n{gövdenin SHA-256'sı}`, imza `v1=` + HMAC-SHA256 (kaynağın anahtarı). Biçim dile bağlı değildir; birim testinde Python ile üretilmiş bir örnek doğrulanır.
- **Kontroller:** bilinmeyen kaynak, yanlış anahtar, değiştirilmiş gövde, ±5 dakika dışındaki zaman damgası ve aynı nonce'un ikinci kez kullanılması 401; kaynağın izni olmayan uç 403; 64 KB üstü gövde 413. Hatalı imzada hangi kontrolün başarısız olduğu söylenmez.
- **Kaynak kaydı:** her olay satırında imzası doğrulanan kaynak adı (`baggage_events.source`) saklanır; çıkış süresi worker'ının yazdığı olaylarda `system`.
- **Simülatör:** her isteği yeni bir nonce ile imzalar (`--source-id`, `--source-secret` ya da `AEROBAG_SOURCE_SECRET`). Bilerek gönderilen kopya mesajlar yine API'nin tekilleştirmesine ulaşır. Reddedilen istekler sonuç özetinde `UNAUTHORIZED` / `FORBIDDEN` olarak sayılır.
- **Testler:** imza biçimi, değişen her parçanın imzayı bozması, nonce ve kaynak adı biçimi, simülatör isteğinin doğrulanması (birim); imzasız, yanlış anahtar, bilinmeyen kaynak, değiştirilmiş gövde, zaman penceresi dışı, tekrar oynatma, yeni nonce ile kopya, kapsam dışı uç (entegrasyon, gerçek API).

Bilinen sınırlar:
- Nonce önbelleği bellektedir ve tek örnek varsayar; API yeniden başlarsa son 5 dakikanın nonce'ları unutulur (olaylar yine de idempotent olduğu için tekrar oynatılan olay durumu değiştirmez).
- Ortak gizli anahtar kullanılır (simetrik). Anahtar döndürme ve kaynak başına birden çok anahtar yok. Asimetrik imza ya da mTLS daha güçlü bir seçenektir.
- Demo kaynağının anahtarı kodda ve README'de yazılıdır; yapılandırmada `Ingest:Sources` verilince kabul edilmez.
- Alma uçlarında hız sınırı yok; imzasız istekler gövde okunmadan reddedilir, imzalı ama yoğun gönderime karşı ayrı bir sınır eklenmedi.

## Faz 4.1: JWT ile kimlik doğrulama ve roller

- **Roller:** `passenger`, `ops`, `security`. Jetonlar HS256 imzalı, kısa ömürlü (personel 15 dk, yolcu 30 dk), `iss`/`aud`/süre doğrulanır, saat kayması payı 30 sn.
- **Personel girişi:** `POST /v1/auth/token` (IP başına dakikada 10). Parolalar PBKDF2-SHA256 (600.000 yineleme) ile saklanır, karşılaştırma sabit sürelidir. Yanlış kullanıcı adı ve yanlış parola aynı yanıtı verir; bilinmeyen kullanıcıda da aynı maliyette doğrulama yapılır.
- **Yolcu jetonu:** `/v1/lookup` (PNR + soyad) başarılıysa yanıtta yolcu jetonu döner. Jetonda kişisel veri yoktur, yalnızca o sorgudaki bavullar vardır (W12a'dan beri `etiket@uçuş` çifti olarak).
- **Uç başına politika:** `/v1/flights` ve `/v1/flights/sla` yalnızca personel. `/v1/bags/{etiket}` personel ya da jetonunda o etiket bulunan yolcu (W12a'dan beri uçuşla birlikte); yetkisiz bagaj için 404 (varlığı sızdırılmaz). Canlı kanal (`/hubs/live`) jeton ister; `JoinOps` yalnızca personel, `JoinBags` yalnızca yetkili etiketler.
- **Arayüz:** operasyon panosu giriş formu gösterir; jeton sekme oturumunda (`sessionStorage`) tutulur, 401/403 gelince oturum kapanır.
- **Testler:** parola özeti birim testleri; gerçek API ve PostgreSQL ile yetkisiz erişim, yanlış rol, başka bagaja erişim, değiştirilmiş ve başka anahtarla imzalanmış jeton, canlı kanal grup yetkisi.

Bilinen sınırlar:
- İmza anahtarı verilmezse her açılışta rastgele üretilir; çok örnekli kurulumda ortak anahtar gerekir. Anahtar döndürme (rotation) yok.
- Yenileme jetonu (refresh token) ve jeton iptali yok; jeton süresi dolana kadar geçerlidir. Canlı kanal bağlantısı kurulduktan sonra jeton süresi dolsa da açık kalır.
- WebSocket jetonu sorgu parametresiyle (`access_token`) gelir; ters vekil sunucu kayıtlarında görünebilir.
- `sessionStorage`'daki jeton bir XSS açığında okunabilir; HttpOnly çerez kullanılmadı.
- Demo kullanıcıları ve parolaları kodda ve README'de yazılıdır; yapılandırmada `Auth:Users` verilince devre dışı kalır.

## Söz #2: SLA ve gecikme riski (DEMO hedefleri)

- Uç: `GET /v1/flights/sla`. Uçuş başına ilk ve son bagaj için hedef, gerçekleşen ya da tahmin, ihlal olasılığı, beklenen gecikme (P50/P90) ve durum (MET, ON_TRACK, AT_RISK, LIKELY_BREACH, BREACHED, UNKNOWN).
- Hedefler `flights.sla_first_bag_sec` (900) ve `sla_last_bag_sec` (1800); on-block'tan ölçülür. Rakamlar demo varsayımıdır, yanıtta `assumption: DEMO_ASSUMPTION` yazar.
- İhlal olasılığı: uçuş düzeyindeki modelin P10/P50/P90 kuantillerinden parça parça doğrusal bir dağılım kurulur (yaklaşıklık). Süre geçtikçe "henüz olmadı" koşuluyla güncellenir. Son bagaj, uçuştaki tüm bagajlar banda geçince gerçek değere döner.
- ML kapalıyken tahmin alanları boş, durum UNKNOWN olur; gerçekleşen ya da süresi dolmuş durumlar yine raporlanır.
- ARCHITECTURE 6.2'deki `suggestion` (kaynak artırımı önerisi) uygulanmadı.
- Bu olasılıkların kalibrasyonu (örneğin "%60 dediğimiz uçuşların kaçı gerçekten aştı") henüz ayrıca ölçülmedi.

## Söz #1: bavul bazlı ETA ve fizik

- Pazar `basis` değerleri: HISTORY, FALLBACK (ampirik), ML_FLIGHT (indirme öncesi uçuş düzeyi), ML (bavul bazlı: indirme anı + model saniyesi), PHYSICS (şutta mesafe / bant hızı ± 15 sn; ML kapalıyken de çalışır).
- Bavul düzeyi model yalnızca uçuş özelliklerini kullanır; indirme sırası ve on-block sonrası süre özellikleri kazanç sağlamadı (bkz. EVALUATION.md, "Bavul düzeyi ETA").
- Fizik sonucu simülatörün aynı formülle üretmesi nedeniyle dairesel; gerçek donanım kanıtı değildir.
- Servis uçları: `/v1/predict-bag`, `/v1/outcome-bags`.

## Faz 4, ilk dilim: çıkış okuması ve çıkış süresi

Çalışan parçalar:
- **EXIT taraması:** BPM okuma noktası `EXIT01` gibi bir `EXIT` önekiyle gelirse bavul `EXIT_CLEARED` olur. Mevcut durum makinesi kuralları geçerlidir (monoton, banttan alınmadan gelirse sıra dışı sayılır).
- **Çıkış süresi:** banttan alınma olayından 180 sn sonra bekleyen bir kayıt (`exit_deadlines`) açılır. Süre, olayın kendi zamanına bağlıdır (alınma zamanı değil).
- **Süre dolum worker'ı (`ExitDeadlineService`):** süresi dolan ve hâlâ `CLAIMED_FROM_BELT` olan bavulu `EXIT_OVERDUE` yapar. Durum, olay günlüğü kaydı (`message_type = SYSTEM`, tekil anahtar) ve outbox mesajı tek işlemde yazılır; kayıtlar `FOR UPDATE SKIP LOCKED` ile alınır. İkinci çalıştırma ikinci bir olay üretmez.
- **`EXIT_OVERDUE` son durum değildir:** süre dolduktan sonra çıkış okunursa bavul `EXIT_CLEARED` olur.
- **Simülatör:** `--mode live` çıkış taramalarını varsayılan olarak üretir (`--exit-scans false` ile kapatılır). Bavulların %2'si çıkışta hiç okunmaz, kalanı banttan alındıktan 30 sn + üstel gecikmeyle okunur. Çıkış taramaları ayrı bir rastgele akıştan gelir; CSV ve ML eğitim verisi değişmez.
- **Operasyon panosu:** "Alındı" artık banttan alınan tüm bavulları sayar (çıkış durumları dahil). "Çıkış okundu" ve "Çıkış okunmadı" bunların alt kümeleridir.
- Yolcuya çıkış durumları ayrıca gösterilmez; "banttan alındı" mesajı geçerli kalır.

Tasarımdan farklar ve bilinen sınırlar:
- **`EXIT_CLEARED` burada yalnızca "çıkışta okundu" demektir.** ARCHITECTURE.md'deki anlamı (etiket ve sunulan PNR eşleşti) bu aşamada uygulanmıyor. Yanlış kişi kontrolü (`EXIT_REVIEW`, sessiz inceleme kaydı) Faz 4'ün kalanıdır ve çıkış kapısı sensöründen sunulan PNR'ı gerektirir; tehdit modeli ve KVKK notu da buna dahildir.
- **"Şimdi" sanal saattir:** worker, BSM olmayan olayların en son zamanını kullanır (yolcu ETA'sı ve takılı bavul sayacıyla aynı kabul). Olay akışı durursa saat de durur ve süre dolumları o zamana kadar işlenmez. Örneğin son uçuşun son bavulları, ardından başka olay gelmezse "çıkış okunmadı" olmaz. Gerçek kurulumda gerçek saat kullanılmalıdır.
- Worker ile aynı anda gelen bir çıkış okuması için bavul satırında açık bir kilit yok; sonuç her iki sırada da `EXIT_CLEARED` olur, ama arada fazladan bir `EXIT_OVERDUE` olayı yazılmış olabilir.
- Çıkış süresi sabit 180 sn (`BaggageEventProcessor.ExitDeadline`), yapılandırılabilir değil.

## Faz 3 durumu: dayanıklılık (ölçüt karşılandı, broker yok)

`ARCHITECTURE.md` Faz 3 için DLQ, idempotent tüketici, Kafka uyumlu broker ve gerekirse Redis öngörür. Bitti ölçütü "çift gelen ve sırasız mesajlarda tutarlı durum"dur.

- **Karşılananlar:** ölü mektup tablosu (`dead_letter_messages`), mesaj anahtarına dayalı idempotency (eş zamanlı kopyada bile tek kayıt), sırasız mesajda durumun geri gitmemesi (`IGNORED_STALE`), bagaj başına advisory lock, transactional outbox (en az bir kez, sıralı, 5 denemede bekletme). Hepsi entegrasyon testlidir.
- **Yapılmayan: Kafka uyumlu broker.** Bilinçli sapma: olay iletimi Postgres outbox + SignalR ile yapılıyor. `ARCHITECTURE.md` zaten "ilk sürümde altyapı yükü olmadan aynı garanti" diyor. Tek makinede çalışan bu proje için broker kurmak ek işletme yükü getirirdi. Bedeli: outbox dağıtıcısı tek örnek varsayar ve birden çok tüketici grubunu desteklemez.
- **Yapılmayan: Redis.** Plana göre "gerekirse" idi; ölçülmüş bir ihtiyaç çıkmadı.

## Faz 2: ML tahmini

Çalışan parçalar:
- **Simülatör gerçekçiliği:** gizli ekip hızı, geç gelen ekipman, araba beklemesi, gözlenebilir yer hizmeti firması (`FlightGenerator.cs`). Gürültü parametreleri demo varsayımıdır.
- **`ml/` Python paketi:** uçuş düzeyinde veri, medyan baseline, kuantil LightGBM, rejim A→B kayma testi, özellik çıkarma deneyi, canlı artık düzeltmesi. Sonuçlar ve sınırlar: `docs/EVALUATION.md`.
- **ML servisi (`ml/service`, FastAPI):** `POST /v1/predict`, `POST /v1/outcome`, `GET /health`. Modeller `ml/models/` altında düz metin dosyalardır (pickle yok). Düzeltme durumu bellektedir; yeniden başlayınca sıfırlanır. Kimlik doğrulama yok, yalnızca `127.0.0.1`.
- **.NET entegrasyonu:** `POST /v1/flights/context` (AODB taklidi) uçuş bağlamını `flight_context` tablosuna yazar. Yolcu sorgusu duruma göre şunu verir: indirme öncesi uçuş düzeyi ML penceresi (`ML_FLIGHT`), indirildikten sonra bavul düzeyi ML (`ML`), şutta fizik (`PHYSICS`). Servis kapalı ya da bağlam yoksa indirme/şut durumlarında eski ampirik ETA'ya (`HISTORY`/`FALLBACK`) düşülür; indirme öncesinde tahmin gösterilmez. Biten uçuşların ve bavulların gerçek süreleri `MlOutcomeBackgroundService` ile servise bildirilir.

Bilinen sınırlar:
- Bavul düzeyindeki ML penceresi (ilk bavul P10 → son bavul P90, ortası P50) bir basitleştirmedir ve ayrıca ölçülmemiştir; değerlendirme uçuş düzeyindedir.
- "Biten uçuş" sezgiseldir: bavulların %90'ı banda gelmiş ve sanal saatte 10 dk geçmiş. Kayıp mesajlar son bavulu erken gösterebilir.
- ML servisinin düzeltme durumu tek örnek ve bellektedir.
