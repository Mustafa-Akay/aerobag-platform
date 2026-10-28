# Veritabanı göçleri

Şema bu klasördeki dosyalarla, sırayla kurulur: `0001_init.sql`, `0002_...sql`, ... Dosya adı `NNNN_kisa_ad.sql` biçimindedir (dört haneli sıra, küçük harf, rakam ve alt çizgi). Sıra 1'den başlar ve boşluk olmaz.

- **Uygulanmış bir dosya değiştirilmez.** Göç aracı her dosyanın SHA-256 özetini `schema_migrations` tablosuna yazar; uygulanmış bir dosya değişirse araç durur. Değişiklik yeni bir dosyayla yapılır. Özet satır sonlarından bağımsızdır (Windows CRLF ile Linux LF aynı özeti verir).
- Her dosya kendi işleminde (transaction) uygulanır: dosyanın bir kısmı hata verirse hiçbir değişiklik kalmaz ve sürüm kaydedilmez.
- Göçleri tablo sahibi hesabıyla `AeroBag.Migrator` uygular. API'nin kısıtlı rolü (`aerobag_app`) şemayı değiştiremez; açılışta yalnızca sürümün güncel olduğunu kontrol eder.
- Dosyalar `AeroBag.Infrastructure` derlemesine gömülür; araç, API ve testler aynı listeyi kullanır.

```powershell
dotnet run --project src\AeroBag.Core\AeroBag.Migrator             # bekleyen göçleri uygular
dotnet run --project src\AeroBag.Core\AeroBag.Migrator -- --status # uygulanmış ve bekleyen göçleri listeler
```
