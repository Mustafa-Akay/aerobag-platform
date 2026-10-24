# AeroBag: Uygulama durumu (koda göre)

`ARCHITECTURE.md` hedef tasarımı anlatır. Bu belge, **şu an çalışan kodun** tasarımdan nerede ayrıştığını ve neyin henüz olmadığını açıkça listeler. Çelişki olursa bu belge koda daha yakındır.

Son güncelleme: 24 Ekim 2026. Kapsam: Faz 1 (simülasyon verisiyle uçtan uca).

## Çalışan parçalar

| Parça | Durum |
|---|---|
| Durum makinesi (monoton, ANOMALY, IGNORED_STALE) | Çalışıyor, birim testli |
| Type B ayrıştırıcı (BSM/BUM/BPM, sıkı sınırlar) | Çalışıyor, birim testli |
| Olay işleme (idempotent, bagaj başına advisory lock, dead-letter) | Çalışıyor, entegrasyon testli |
| Yolcu sorgusu (PNR + soyad, HMAC, tekdüze 404, IP başına 20/dk) | Çalışıyor |
| ETA tahmini (deneysel P10/P50/P90) | Çalışıyor, canlı doğrulandı |
| Operasyon panosu API'si ve arayüzü | Çalışıyor |
| Outbox → SignalR canlı bildirim | Çalışıyor, entegrasyon testli |
| React arayüzü (yolcu sayfası, operasyon panosu) | Çalışıyor |
| Simülatör (CSV ve canlı mod, tohumlu, gürültü ayarlı) | Çalışıyor |

## Tasarımdan ayrışan ve netleşen noktalar

- **BPM okuma noktası kuralı.** Biçim `[A-Z]{3,10}\d{2}` (iki rakam şart). Önek durumu belirler: `SEC` güvenlik, `LOAD` uçağa yükleme, `CHUTE` şut, `CAROUSEL` bant ağzı, `CLAIM` banttan alma. Bilinmeyen önek reddedilir.
- **Olay zamanı.** BSM'nin zamanı mesajda yoktur, alınma anı kullanılır. BUM ve BPM zamanı mesajdan gelir.
- **ETA tabanı.** Olay geçmişinden `percentile_cont` ile hesaplanır. En az 20 örnek varsa `HISTORY`, yoksa sabit varsayılanlar (`FALLBACK`: iniş→bant 300/540/900 sn, şut→bant 60/90/150 sn). ETA yalnızca bagaj indirildikten sonra verilir.
- **Referans an (`asOfUtc`).** Simülasyonda "şimdi", BSM olmayan olayların en son zamanıdır (sanal saat). Gerçek kurulumda gerçek saat olmalıdır.
- **Yolcu verisi.** Ad, soyad ve PNR düz metin saklanmaz; yalnızca `HMAC-SHA256(gizli anahtar, PNR|soyad)` arama anahtarı tutulur. Geliştirme varsayılan anahtarı kodda durur; gerçek kurulumda `Security__LookupKeySecret` verilmelidir.
- **Bant numarası** saklanmıyor, arayüzde gösterilmiyor (AODB bağlamı yok).
- **Canlı bildirim.** `/hubs/live` kanalı içeriksizdir: `bagChanged` (grup `bag:{etiket}`) ve `flightsChanged` (grup `ops`). İstemci ayrıntıyı REST ile yeniden çeker. Canlı bağlantı varken yoklama 20 sn'ye, yokken 3 sn'ye ayarlıdır.
- **Outbox garantisi.** En az bir kez teslim, `seq` sırası, 5 başarısız denemeden sonra mesaj bekletilir. Tek örnek varsayılır.

## Bilinen eksikler ve riskler

1. **Kimlik doğrulama yok:** `/v1/events`, `/v1/flights` ve `/hubs/live` herkese açık. Yalnızca yerel demo için uygundur. Hub'da bagaj etiketini bilen o bagajın "değişti" sinyalini alabilir (içerik görmez). Faz 4.
2. **Çıkış doğrulama yok.** `exit_deadlines` satırları yazılıyor ama hiçbir mesaj `EXIT_CLEARED` üretemiyor (mapper'da eşleme yok, simülatör göndermiyor) ve süre dolunca durumu değiştiren worker yok. Panodaki "Alındı" sayacı yalnızca `CLAIMED_FROM_BELT` sayar. Faz 3.
3. **Bantta takılı bagaj tespiti yok.** "Alındı" mesajı düşerse bagaj sonsuza dek `ON_CAROUSEL` kalır; çıkış süresi bunu çözmez çünkü sayaç alınma anında başlar.
4. **AODB bağlamı alınmıyor:** uçak tipi, park mesafesi, hava durumu, bant yok. ML aşamasından (Faz 2) önce gerekir.
5. **ETA taban çizgisi ölçülmedi.** Doğruluk, kapsama ve rejim kayması değerlendirmesi Faz 2'dir.
6. **Doğrulama kapsamı:** sonuçlar yalnızca simülasyon verisiyle doğrulandı. Gerçek havalimanı verisi yoktur ve IATA BIX uyumluluğu iddia edilmez.
7. **Bağımlılık uyarısı:** `SSH.NET` (Testcontainers üzerinden, yalnızca test projesinde) için NU1903 uyarısı var. Üretime girmez; Testcontainers güncellenince kalkması beklenir.
8. **CI henüz bir GitHub deposunda çalıştırılmadı.** `.github/workflows/ci.yml` yazıldı, ilk koşu doğrulanmalıdır.

## Yerelde çalıştırma

```powershell
docker compose up -d                                   # Postgres (5433)
dotnet run --project src\AeroBag.Core\AeroBag.Api --urls http://localhost:5080
cd src\aerobag-ui; npm install; npm run dev            # http://localhost:5173
dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --speed 5
dotnet test                                            # Docker açık olmalı
```

Şema değişince veritabanı `docker compose down -v` ile sıfırlanır. Docker açıkken ve Postgres hazırken API'yi başlatın; hazır değilken gönderilen olaylar kaybolur ve sonraki olaylar "sıra dışı" görünür.
