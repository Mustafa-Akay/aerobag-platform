// Her test sınıfı kendi PostgreSQL kapsayıcısını başlatır. Hepsi aynı anda başlarsa Docker Desktop
// (özellikle WSL2 ve sınırlı bellekte) "InternalServerError / BadGateway" ile yanıt verebiliyor.
// Aynı anda en çok 3 sınıf çalışır; testler biraz uzar ama kararlı olur.
[assembly: CollectionBehavior(MaxParallelThreads = 3)]
