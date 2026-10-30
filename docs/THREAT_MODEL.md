# AeroBag: Tehdit modeli

Bu belge, **şu an çalışan kodun** güvenlik durumunu anlatır: her tehdit için ne yapıldığı, hangi testle doğrulandığı ve neyin açık kaldığı. Tasarım hedefi `ARCHITECTURE.md` bölüm 9'dadır; bu belge onunla çelişirse koda daha yakın olan bu belgedir.

**Kapsam ve varsayımlar.** AeroBag simülasyon verisiyle çalışan bir portfolyo projesidir; gerçek yolcu verisi, gerçek havalimanı sistemi ya da üretim kurulumu yoktur. API yerel makinede HTTP ile çalışır; TLS, ters vekil sunucu, gizli anahtar kasası ve çok örnekli kurulum kapsam dışıdır. Aşağıdaki "açık" maddelerin bir kısmı bu varsayımların doğrudan sonucudur.

## 1. Sistem ve güven sınırları

```
 Yolcu tarayıcısı ──(PNR+soyad, POST)──┐
 Personel tarayıcısı ──(JWT)───────────┤
                                       ▼
 Simülatör / AODB / kapı sensörü ──(HMAC imzalı)──▶  API (.NET)  ──(kısıtlı rol: aerobag_app)──▶ PostgreSQL
                                       ▲                 │
                         canlı kanal (SignalR, JWT) ─────┘ └──(yalnızca 127.0.0.1)──▶ ML servisi (FastAPI)
```

| Aktör | Kimlik doğrulama | Erişebildiği |
|---|---|---|
| Yolcu | PNR + soyad (`POST /v1/lookup`), sonra 30 dk'lık yolcu jetonu | Yalnızca kendi bavulları (sorgu, `/v1/bags/{etiket}`, canlı kanalda kendi bavul grupları) |
| Operasyon personeli | Kullanıcı adı + parola, 15 dk'lık JWT (`ops`) | Operasyon panosu, SLA, tüm bavullar |
| Güvenlik personeli | Kullanıcı adı + parola, 15 dk'lık JWT (`security`) | Yukarıdakiler + çıkış inceleme kayıtları ve sonuçlandırma |
| Mesaj kaynağı (simülatör, AODB, kapı) | Kaynak başına HMAC imzası | Yalnızca izinli kapsamdaki alma uçları (`events`, `context`, `gate`) |
| API'nin veritabanı hesabı | `aerobag_app` rolü | Kodun kullandığı işlemler; hiçbir tabloda DELETE yok |

## 2. STRIDE

### Spoofing (kimlik taklidi)

| Tehdit | Önlem | Test |
|---|---|---|
| Sahte kaynaktan bavul olayı, uçuş bağlamı ya da kapı paketi göndermek | Kaynak başına HMAC-SHA256 imzası (kaynak, zaman, nonce, yöntem, yol, gövde özeti); bilinmeyen kaynak ve yanlış anahtar 401 | `IngestSignatureTests` (imzasız, yanlış anahtar, bilinmeyen kaynak), `GateTests` (imzasız kapı paketi) |
| Bir kaynağın izni olmayan uca yazması (ör. AODB'nin bavul olayı göndermesi) | Kaynak başına kapsam; kapsam dışı uç 403 | `IngestSignatureTests.Source_can_write_only_to_its_scopes`, `GateTests` |
| Personel kimliğine bürünmek | PBKDF2-SHA256 (600.000 yineleme) parola özeti, sabit süreli karşılaştırma, IP başına dakikada 10 giriş denemesi | `PasswordHasherTests`, `AuthTests.Wrong_password_and_unknown_user_get_the_same_answer` |
| Sahte ya da değiştirilmiş JWT | HS256, `iss`/`aud`/süre doğrulaması, yalnızca HS256 kabul | `AuthTests.Tampered_or_foreign_tokens_are_rejected` |

Açık kalanlar:
- Gizli anahtarlar simetrik (HMAC ve JWT); anahtar döndürme yok. mTLS ya da asimetrik imza daha güçlüdür.
- Demo kullanıcıları, demo kaynağı ve geliştirme anahtarları kodda ve README'de yazılıdır; yapılandırmada gerçek değerler verilince devre dışı kalırlar.
- Yolcunun "kimliği" PNR + soyaddır. PNR'ı ve soyadı bilen herkes o yolcunun bavul durumunu görebilir (havayolu sitelerindeki yaygın modelle aynıdır).

### Tampering (kurcalama)

| Tehdit | Önlem | Test |
|---|---|---|
| Geçmiş bavul olaylarını değiştirmek ya da silmek | Olay günlüğü ve kapı okumaları yalnızca eklemeli: uygulama rolünde UPDATE/DELETE/TRUNCATE yetkisi yok; tablo sahibi için de tetikleyici engeli | `AppendOnlyTests` (uygulama rolü ve tablo sahibi) |
| Aktarım sırasında mesaj gövdesini değiştirmek | Gövde özeti imzanın parçası | `IngestSignatureTests.Body_changed_after_signing_is_rejected` |
| Eski bir isteği tekrar oynatmak | ±5 dk zaman penceresi ve nonce önbelleği; ayrıca olaylar idempotent | `IngestSignatureTests` (zaman penceresi, tekrar oynatma, yeni nonce ile kopya) |
| Bavulun durumunu geri almak | Durum makinesi monotondur; geride kalan olay durumu değiştirmez | `BagStateMachineTests`, `BaggageEventProcessorTests` |
| Bilinen açığı olan bir bağımlılık (tedarik zinciri) | CI her itmede NuGet (dolaylı bağımlılıklar dahil), npm (çalışma zamanı, yüksek ve kritik) ve pip paketlerini bilinen açıklara karşı denetler; açık varsa iş kırmızıya döner. 29 Ekim 2026: SignalR (CVE-2026-70159) ve SSH.NET açıkları güncellemeyle kapatıldı | CI: "NuGet/npm/pip güvenlik denetimi" adımları |

Açık kalanlar:
- Süper kullanıcı tetikleyiciyi kapatabilir; veritabanı yöneticisine karşı olay zinciri özeti (hash chain) ya da harici değiştirilemez kayıt yok.
- Nonce önbelleği bellektedir, tek örnek varsayar ve yeniden başlatmada sıfırlanır.
- Yerel kurulumda TLS yok; ağ üzerinde jeton ve gövde açık gider.
- Bağımlılık denetimi yalnızca bilinen açıkları yakalar; sürümler kilitlenmediği için (`>=`) ML bağımlılıkları kurulumdan kuruluma değişebilir. npm denetimi geliştirme araçlarını kapsamaz.

### Repudiation (inkâr)

| Tehdit | Önlem | Test |
|---|---|---|
| "Bu olayı biz göndermedik" | Her olayda imzası doğrulanan kaynak adı (`baggage_events.source`), alınma zamanı ve idempotency anahtarı | `IngestSignatureTests.Signed_event_is_accepted_and_its_source_is_recorded` |
| Kapıda ne olduğunun sonradan tartışılması | Her kapı okuması kararıyla birlikte `gate_reads`'e (yalnızca eklemeli) yazılır | `GateTests` |
| İnceleme kaydını kimin kapattığı | `resolved_by` (jetondaki kullanıcı), zaman, not | `GateTests.Someone_else_triggers_a_silent_review...` |

Açık kalanlar:
- `ARCHITECTURE.md`'deki `CorrelationId` uygulanmadı; izleme idempotency anahtarı ve kaynak üzerinden yapılır.
- Personel girişleri ve pano görüntülemeleri ayrı bir denetim günlüğüne yazılmıyor.

### Information disclosure (bilgi sızması)

| Tehdit | Önlem | Test |
|---|---|---|
| PNR + soyad deneyerek başka yolcunun bilgisine ulaşmak | IP başına dakikada 20 sorgu; "kayıt yok", "PNR yanlış" ve "soyad yanlış" aynı yanıtı verir; yanıtta ad ve PNR yok | `PassengerLookupTests` |
| Veritabanı sızarsa yolcu kimliklerinin çıkması | Ad, soyad ve PNR saklanmaz; yalnızca gizli anahtarlı HMAC özetleri. Ham Type B metninde ad (`.P/`) ve PNR (`.L/`) satırları maskelenir, ölü mektup hata metninden de temizlenir | `PassengerLookupTests.Passenger_is_stored_only_as_hmac_key...`, `AppendOnlyTests` (ham metin, ölü mektup), `TypeBRedactorTests` |
| Kapıda sunulan PNR'ın saklanması | Sunulan PNR yalnızca bellekte özetlenir; hiçbir tabloya yazılmaz | `GateTests.Owner_is_cleared_and_the_presented_pnr_is_never_stored` (tüm tablolarda arama) |
| Yolcu jetonuyla başkasının bavulunu görmek | Yolcu jetonu yalnızca sorgudaki bavulları `etiket@uçuş` çifti olarak taşır; başka bavul 404 (varlığı sızdırılmaz); canlı kanalda başka bavul grubuna katılamaz | `AuthTests.Passenger_token_sees_only_its_own_bag`, `AuthTests.Live_channel_requires_a_token_and_checks_groups` |
| Yeniden kullanılan etiketle başka günün bavulunu görmek (etiket numaraları zamanla yeniden verilir) | Jeton iddiası ve canlı kanal grubu uçuşa bağlıdır (`bag:etiket@uçuş`); yolcu için `/v1/bags/{etiket}` uçuşu jetondan alır, başka uçuş istenirse 404 | `AuthTests.Passenger_token_is_bound_to_the_flight_not_only_the_tag` |
| PNR ve soyadın sunucu kayıtlarına düşmesi | Sorgu `POST` gövdesindedir (adreste değil); istek adresini yazan kayıtlar uyarı düzeyine çekildi | Kod incelemesi (otomatik test yok) |
| Canlı kanalın içerik sızdırması | Bildirimler içeriksizdir ("değişti"); ayrıntı yetkili uçtan çekilir | `OutboxRelayTests` |
| Saklama süresi dolmuş verinin kalması | Teslimden 30 gün sonra arama özetleri rastgele değerle değiştirilir; yolcu artık bulunamaz | `RetentionTests` |

Açık kalanlar:
- Dağıtık IP'lerden PNR + soyad denemesine karşı IP başına sınır yetersizdir (CAPTCHA, hesap düzeyi sınır yok).
- Canlı kanal jetonu WebSocket için adreste (`access_token`) gider; API kayıtları kapatıldı ama bir ters vekil sunucu bunu kaydedebilir.
- Personel jetonu tarayıcıda `sessionStorage`'dadır; arayüzde bir XSS açığı olursa okunabilir (HttpOnly çerez kullanılmadı).
- ML servisi kimlik doğrulaması istemez; yalnızca `127.0.0.1`'e açıktır.

### Denial of service (hizmet engelleme)

| Tehdit | Önlem | Test |
|---|---|---|
| Yolcu sorgusu ya da giriş ucuna yoğun istek | IP başına sabit pencere sınırı (sorgu 20/dk, giriş 10/dk) | `AuthTests` (dolaylı); canlıda doğrulandı |
| Alma uçlarına büyük gövde | 64 KB sınırı (413); Type B metni uzunluk sınırlı | `TypeBParserTests` |
| Ayrıştırılamayan mesajların sistemi durdurması | Ölü mektup tablosuna yazılır, işleme devam eder | `BaggageEventProcessorTests` |

Açık kalanlar:
- Alma uçlarında hız sınırı ve geri basınç yok; imzalı ama yoğun gönderim veritabanını yorabilir.
- Tek örnek; yük dengeleme ve yatay ölçekleme yok.

### Elevation of privilege (yetki yükseltme)

| Tehdit | Önlem | Test |
|---|---|---|
| Yolcu jetonuyla operasyon uçlarına erişmek | Uç başına politika; yolcu jetonu personel uçlarında 403 | `AuthTests.Passenger_token_cannot_reach_ops_endpoints` |
| Operasyon personelinin güvenlik kayıtlarını görmesi ya da kapatması | Güvenlik uçları yalnızca `security` rolü | `GateTests` (ops 403) |
| API ele geçirilirse veritabanında geniş yetki | En az yetkili rol: hiçbir tabloda DELETE yok, ölü mektuplar okunamaz, tablo oluşturulamaz, yolcu satırında yalnızca özet sütunları güncellenebilir | `AppendOnlyTests`, `RetentionTests.App_role_can_change_only_the_key_columns_of_passengers` |

Açık kalanlar:
- Kullanıcı yönetimi yok; kullanıcılar yapılandırmadan gelir.

## 3. Çıkış doğrulamaya (ExitGuard) özgü riskler

| Risk | Durum |
|---|---|
| Bavulu başkası alıyor | PNR uyuşmazlığında sessiz inceleme kaydı. Simülasyonda (varsayılan eşik 0,60) vakaların %87'si yakalandı, hiçbiri sahip diye geçmedi (`EVALUATION.md`). |
| Yanlış alarm yükü | Vaka nadir olduğu için açılan incelemelerin yalnızca yaklaşık %24'ü gerçek (taban oran etkisi). Bu yüzden kapıda alarm yok; sonuç personele bırakılır. Oranlar sensör varsayımlarına bağlıdır. |
| Aynı rezervasyondaki herkes birbirinin bavulunu alabilir | Bilinçli kural (aile). PNR'ı ele geçiren biri de bu kuraldan geçer. |
| Etiket sökme ya da değiştirme | Tek okuyucuyla tespit edilemez; kural bunu yakalamaz. |
| Zayıf okuma | Karar verilmez (`NO_DECISION`); bavul çıkış süresi dolunca "çıkış okunmadı" olur. Bu, alarm yerine operasyon yükü üretir. |
| Yan koridordan bavul okunması | Yanlış alarm üretir ve o bavulu `EXIT_REVIEW`'a düşürür; eşikle azalmaz, fiziksel önlem gerekir. |
| Yolcuya bildirim | Yalnızca bilgilendirme ("beklenmedik bir çıkış kaydı... bagaj hizmetleriyle iletişime geçin"); müdahaleye teşvik eden dil yok. |

## 4. Sızma ve yanlış alarm testlerinin özeti

Otomatik güvenlik testleri gerçek API ve gerçek PostgreSQL ile (Testcontainers) çalışır ve CI'da her değişiklikte koşar:

| Sınıf | Neyi dener |
|---|---|
| `AuthTests` | Jetonsuz erişim, yanlış parola ve bilinmeyen kullanıcının aynı yanıtı, rol ihlali, başkasının bavulu, yeniden kullanılan etiketle başka uçuşun bavulu, değiştirilmiş ve başka anahtarla imzalanmış jeton, canlı kanal grup yetkisi |
| `IngestSignatureTests` | İmzasız, yanlış anahtar, bilinmeyen kaynak, değiştirilmiş gövde, zaman penceresi dışı, tekrar oynatma, kapsam dışı uç |
| `AppendOnlyTests` | Uygulama rolü ve tablo sahibiyle olay günlüğünü değiştirme/silme, her tabloda silme, ölü mektup okuma, tablo oluşturma; ham metinde kişisel veri |
| `GateTests` | Sahip, aile, başkası, zayıf sinyal, bilinmeyen etiket, sıra dışı okuma, tekrar paket, imza/kapsam, geçersiz paket, bayrak kapalı, sunulan PNR'ın hiçbir tabloda bulunmaması, inceleme yetkisi |
| `RetentionTests` | Saklama süresi, teslim edilmemiş bavulun korunması, yolcu satırında sütun düzeyi yetki |

Yanlış alarm ölçümü: `EVALUATION.md`, "ExitGuard" bölümü (`--mode gate-eval`).

Bu testler bilinen saldırı yollarını dener; bağımsız bir sızma testi ya da kod denetimi yerine geçmez.
