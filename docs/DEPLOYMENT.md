# Menjalankan dan menyimpan data

## Lokal dengan Docker

```powershell
.\scripts\Start.ps1
```

Script membuat `.env` hanya bila belum ada, memeriksa Linux engine, lalu menjalankan Compose dan readiness check. Default HTTP aplikasi `127.0.0.1:8150`, PostgreSQL `127.0.0.1:5450`. Project Compose `retailops` mempunyai volume `postgres_data` dan `app_keys` tersendiri. `Stop.ps1` menghentikan layanan milik project ini dan mempertahankan volume.

Workspace baru tidak memuat akun admin bawaan. Selesaikan setup melalui UI, lalu buat akun dengan role dan assignment yang sesuai. Data demo pada screenshot menggunakan identitas Training dan domain `.test`; password lokal tidak masuk Git.

## Pengembangan Windows

SDK 10.0.403 sudah dipatok pada `global.json`. Helper mencoba `RETAILOPS_DOTNET`, `D:\DevTools\dotnet\dotnet.exe`, kemudian executable pada PATH. Bila `D:\DevCache` tersedia, cache CLI/NuGet diarahkan ke sana.

```powershell
.\scripts\init-env.ps1
.\scripts\dev.ps1
```

Jalankan initializer satu kali saja; ia menolak menimpa credential yang ada. Mode development memakai Vite pada5191 dengan proxy ke API5190. Mode ini membantu perubahan source, tetapi pembuktian offline memakai production build yang disajikan backend, karena service worker tidak diaktifkan pada Vite dev server.

Jika `.env` hilang sementara volume project masih ada, initializer menolak membuat credential pengganti. Password baru tidak otomatis mengganti password role PostgreSQL yang sudah tersimpan. Pulihkan file credential asli; startup tidak menghapus atau menginisialisasi ulang database untuk menyembunyikan masalah autentikasi.

Lokasi repo di D: menentukan lokasi source dan berkas lokal aplikasi. Lokasi disk Docker Desktop menentukan tempat volume/image sebenarnya; memindahkan repo saja tidak memindahkan disk Docker.

## Test database

Initializer menyediakan `RETAILOPS_TEST_CONNECTION` yang menunjuk role pengembangan dengan hak membuat database. Harness menolak nama dasar di luar prefix `retailops_test`, membuat database UUID baru per tes, lalu hanya menghapus database yang dibuatnya sendiri. Database bisnis `retailops` tidak menjadi database test. Jangan membagikan `.env` atau menampilkan connection string pada tangkapan layar.

## Deployment HTTPS

Konfigurasi `compose.production.yml` menambahkan Caddy, secure cookies, proxy tepercaya dan role runtime dengan privilege terbatas. Siapkan host/domain dan credential terpisah:

```powershell
.\scripts\init-env.ps1 -Production
```

Isi `RETAILOPS_DOMAIN` dan `CADDY_EMAIL` pada `.env.production`, lalu periksa konfigurasi:

```powershell
docker compose --env-file .env.production -f compose.yml -f compose.production.yml config --quiet
docker compose --env-file .env.production -f compose.yml -f compose.production.yml up --build -d
```

Untuk konfigurasi internet, operator harus menyelesaikan bootstrap melalui jalur yang dibatasi terlebih dahulu, mengelola DNS/sertifikat, meninjau firewall serta backup, dan memakai secret production yang berbeda. Runtime tidak menjalankan migrasi schema; service migrator melakukannya sebelum app siap. Jangan mengganti nama domain/trusted proxy tanpa menyesuaikan jaringan deployment.

Konfigurasi tersedia untuk operator; keberhasilan lokal tidak membuktikan sertifikat atau DNS host publik. Tidak ada layanan .NET yang dipublikasikan otomatis ke GitHub Pages.

## Backup

```powershell
.\scripts\backup.ps1
```

Script menjalankan `pg_dump --format=custom` dalam container project dan menyalin hasil ke `.local/backups`, kemudian menghapus file sementara dump miliknya dalam container. Dump memuat akun, hash password dan data bisnis; simpan sebagai data privat. Data Protection key volume juga diperlukan untuk kesinambungan cookie yang dilindungi.

Uji pemulihan ke database/container terpisah sebelum mengandalkan backup. Pengujian transaksi, screenshot atau backup yang berhasil dibuat bukan bukti otomatis bahwa prosedur restore operator sudah diuji.
