# Asal source

RetailOps dikembangkan dari source milik pemilik repo pada [StockPilot](https://github.com/zalamanga/stockpilot-dotnet/tree/0c65abc943d3a825bf431e374f5ed83c06e9e1a9). Fondasi yang digunakan mencakup autentikasi sesi, CSRF, pembelian/persetujuan, penerimaan, penguncian stok, UI back-office, dan pengujian PostgreSQL.

Source diambil dari file yang dilacak Git saja. Database, akun, password, runtime, metadata Git dan screenshot StockPilot tidak disalin. RetailOps memakai nama aplikasi, cookie, database, volume dan port tersendiri. Repo StockPilot tetap menjadi proyek terpisah.

[ReserveFlow](https://github.com/zalamanga/reserveflow-api) menjadi referensi pola penguncian transaksi dan replay idempotensi; implementasi penjualan RetailOps berada di C# dalam backend yang sama. Backend Go dan data contoh tenant ReserveFlow tidak digabungkan sebagai layanan tambahan.

Font IBM Plex Sans yang diwarisi mempertahankan [lisensi SIL Open Font](licenses/IBM-Plex-OFL.txt). Dokumentasi dan screenshot RetailOps harus menunjukkan perilaku implementasi RetailOps yang benar-benar diuji.
