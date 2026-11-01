# Membaca dan membangun RetailOps dari fondasinya

Panduan ini menjelaskan urutan pembuatan, tanggung jawab file dan alasan aturan bisnisnya. Buka [alur aplikasi](FLOWCHARTS.md) untuk gambaran proses, lalu baca source sesuai urutan berikut. Nama file di sini merujuk implementasi yang ada, bukan daftar fitur yang belum dibuat.

## 1. Mulai dari solution dan versi runtime

[RetailOps.slnx](../RetailOps.slnx) mengelompokkan API dan test project. [global.json](../global.json) mematok SDK supaya build lokal/CI menggunakan keluarga toolchain yang sama. File project [RetailOps.Api.csproj](../backend/RetailOps.Api/RetailOps.Api.csproj) menyebut target net10.0 dan paket EF/Npgsql/OpenAPI. `packages.lock.json` mengunci hasil dependency restore.

Frontend berada pada [package.json](../frontend/package.json) dan lockfile npm. React menampilkan aplikasi, TypeScript memeriksa bentuk data, TanStack Query mengelola query online, sedangkan IndexedDB menangani data offline yang memang perlu bertahan setelah reload. Antrean bukan sekadar state React.

## 2. Susun HTTP pipeline

[Program.cs](../backend/RetailOps.Api/Program.cs) mendaftarkan DbContext, password service, cookie authentication, authorization policy, CSRF, rate limit dan OpenAPI. Middleware kemudian membentuk respons error ProblemDetails, memasang header, menjalankan autentikasi/otorisasi, serta memvalidasi mutation sebelum endpoint domain berjalan.

Urutannya penting: kode domain tidak boleh bergantung pada tombol UI yang disembunyikan. Peran Cashier mempunyai policy retail dan tidak masuk policy Backoffice yang melindungi route bisnis lama. Server juga membatasi body dan menerima JSON untuk request yang mempunyai body.

`--migrate` membuat proses hanya menjalankan migrasi lalu keluar. Pada Compose, service migrator menyelesaikan langkah ini sebelum app siap. `/health/live` menunjukkan proses berjalan; `/health/ready` memeriksa koneksi database. Keduanya tidak membuktikan seluruh alur bisnis sudah benar, sehingga tetap diperlukan tes transaksi/browser.

## 3. Pisahkan autentikasi dari izin bisnis

[AuthEntities.cs](../backend/RetailOps.Api/Auth/AuthEntities.cs) memuat User, UserSession, Organization dan RoleNames. [AuthEndpoints.cs](../backend/RetailOps.Api/Auth/AuthEndpoints.cs) menangani setup, login, me, logout dan administrasi akun. UserView menyertakan organizationId yang stabil untuk partisi offline.

[PasswordSecurity.cs](../backend/RetailOps.Api/Security/PasswordSecurity.cs) menangani password hash dan verifikasi. [SessionAuthentication.cs](../backend/RetailOps.Api/Security/SessionAuthentication.cs) membaca credential sesi opaque, memeriksa hash/sesi/akun pada database, dan membentuk identitas request. Ini bukan JWT atau OAuth server.

CSRF mengikat mutation ke sesi browser. CSRF saja belum cukup untuk antrean lintas tab: cookie dapat berubah setelah pemeriksaan `/auth/me`. Karena itu queued request juga mengirim expected actor/organization; server menolak jika pemilik perintah berbeda dari sesi saat POST benar-benar diproses.

## 4. Buat model dan constraint sebelum form

[BusinessEntities.cs](../backend/RetailOps.Api/Domain/BusinessEntities.cs) mempertahankan product, supplier, warehouse, purchase order, receipt, stock balance/movement dan audit dari fondasi pembelian. [RetailEntities.cs](../backend/RetailOps.Api/Domain/RetailEntities.cs) menambahkan cabang, assignment, harga, shift, sale, return, replay command dan financial journal.

[AppDbContext.cs](../backend/RetailOps.Api/Data/AppDbContext.cs) menyediakan DbSet dan memanggil konfigurasi model. [BusinessModel.cs](../backend/RetailOps.Api/Data/BusinessModel.cs), [AuthModel.cs](../backend/RetailOps.Api/Auth/AuthModel.cs), dan [RetailModel.cs](../backend/RetailOps.Api/Data/RetailModel.cs) menentukan foreign key, precision, unique index serta check constraint.

[RetailLedger migration](../backend/RetailOps.Api/Data/Migrations/20261101141403_RetailLedger.cs) menyimpan perubahan schema yang bisa dipasang ulang secara terurut. Constraint menjaga saldo stok tidak negatif, jumlah/uang valid, satu open shift per user, source journal unik dan hubungan record tetap sah. Migration juga memuat fungsi/trigger PostgreSQL untuk financial journal; bukan seluruh aturan diletakkan pada C# saja.

## 5. Definisikan request yang boleh dikirim klien

[RetailContracts.cs](../backend/RetailOps.Api/Services/RetailContracts.cs) memisahkan request BranchWrite, PriceWrite, ShiftWrite, SaleWrite dan ReturnWrite. SaleWrite membawa productId, quantity, unitPrice dan priceVersion sebagai snapshot yang diperiksa. Klien tidak menentukan nilai COGS, total sah atau journal lines.

[RetailEndpoints.cs](../backend/RetailOps.Api/Endpoints/RetailEndpoints.cs) memasang route dan policy, lalu menyerahkan pekerjaan ke RetailService. Filter expected-owner berlaku pada mutation retail yang mengirim pasangan header. Parameter GUID dan DTO yang jelas membantu binding/OpenAPI, tetapi validasi bisnis tetap dilakukan service.

Lihat [API](API.md) untuk route dan [kontrak lengkap](RETAIL-CONTRACT.md) untuk body/response. `Idempotency-Key` diperlukan pada open/close shift, sale dan return; konfigurasi harga memakai expectedVersion, sedangkan assignment memakai natural key.

## 6. Jalankan satu sale dalam satu transaksi

[RetailService.cs](../backend/RetailOps.Api/Services/RetailService.cs) memuat operasi tulis:

| Function | Alasan dan hasil |
|---|---|
| `Authorize` | Memeriksa akun aktif, peran dan assignment cabang terkini |
| `ActorLock` | Menserialkan perubahan yang bergantung pada actor yang sama |
| `BeginKey` | Mengunci key, membandingkan owner/kind/fingerprint dan mencari respons lama |
| `Finish` | Menyimpan respons replay bersama efek domain, lalu commit |
| `CreateBranch` / `Assign` | Memetakan gudang kosong ke cabang dan mengelola akses Cashier |
| `SetPrice` | Memeriksa versi harga dan menyimpan kenaikan version |
| `OpenShift` / `CloseShift` | Mengelola shift pribadi dan mencatat hitungan kas yang stabil |
| `Sell` | Memeriksa snapshot harga/shift/stok lalu menulis sale, movements, GL dan audit |
| `Return` | Membatasi retur kumulatif dan membalik harga/cost asli dalam satu transaksi |

`Sell` mengurutkan line menurut productId sebelum fingerprint/locking. Duplikasi product ditolak. Setelah role dan branch sah, service memeriksa replay sebelum mensyaratkan shift masih terbuka untuk efek baru. Dengan demikian retry atas sale yang sudah commit tetap dapat mengembalikan struk meskipun shift kemudian ditutup.

Untuk efek baru, shift dikunci, harga dan produk diperiksa, lalu posisi inventory dikunci dalam urutan stabil. Server menghitung total, mengecek cashTendered dan stock-on-hand, kemudian membuat snapshot sale. Jika harga lokal berbeda, server mengembalikan konflik tanpa menulis sale atau stok.

Fingerprint adalah representasi kanonis request yang bermakna, termasuk owner dan kind. Key yang sama tidak boleh dipakai untuk mengganti jumlah/harga/shift secara diam-diam. Respons yang tersimpan merupakan bukti hasil perintah awal, bukan instruksi untuk menjalankan sale lagi.

## 7. Hubungkan stock ledger dan financial journal

[Accounting.cs](../backend/RetailOps.Api/Services/Accounting.cs) mempunyai `Post`/`PostAt` untuk menambahkan journal seimbang, menghilangkan komponen bernilai nol dan memeriksa tanda debit/kredit. Ia tidak melakukan commit sendiri; caller tetap berada dalam transaksi bisnis yang sama.

Contoh makna penjualan 100.000 dengan COGS 60.000:

| Akun | Debit | Kredit |
|---|---:|---:|
| Cash | 100.000 | 0 |
| Sales revenue | 0 | 100.000 |
| COGS | 60.000 | 0 |
| Inventory | 0 | 60.000 |

Ini contoh aturan, bukan hasil transaksi pelanggan. Debit dan kredit sama 160.000, tetapi jumlah penjualan tetap 100.000; menjumlahkan seluruh debit sebagai omzet akan salah.

`BusinessMutations` yang diwarisi juga memanggil accounting pada receipt, adjustment dan transfer. Nilai stok memakai standard cost; utang receipt memakai harga PO aktual. Selisih masuk akun 5100. Perubahan StandardCost ditolak selama stok masih ada dan memakai penguncian produk yang sama dengan operasi stok.

Retur membalik harga dan COGS asli. Jika produk pernah habis, cost diubah, lalu barang diretur, nilai inventory yang kembali memakai cost saat ini dan perbedaannya dicatat sebagai variance. Mengubah journal penjualan lama akan merusak sejarah dan tidak dilakukan.

## 8. Pertahankan journal setelah posting

EF guard menolak perubahan/penghapusan record append-only. PostgreSQL menambahkan lapisan yang tetap berlaku ketika EF dilewati: trigger menolak UPDATE/DELETE/TRUNCATE pada journal dan chart account. Penambahan line pada header yang sudah commit juga ditolak melalui PostingTransactionId native.

Deferred constraint memeriksa header/line pada akhir transaksi, sehingga journal setengah jadi boleh berada sementara dalam transaction miliknya tetapi tidak dapat commit. Minimal dua line positif dan jumlah debit sama kredit harus terpenuhi. Unique sourceType/sourceId mencegah posting kedua untuk sumber bisnis yang sama.

## 9. Hitung laporan dari snapshot yang konsisten

[RetailQueries.cs](../backend/RetailOps.Api/Services/RetailQueries.cs) melayani branch/catalog/shifts/sales/journals dan Reports. Filter Cashier diterapkan di query, bukan setelah seluruh data dikirim ke browser.

`Reports` memakai transaksi RepeatableRead untuk seluruh agregat agar sale yang commit di tengah pembacaan tidak membuat omzet berasal dari waktu berbeda dengan COGS/trial balance. `Shifts` menghitung expected cash open shift dari accepted sales/refunds dalam snapshot baca yang sama; closed shift mempertahankan hitungan penutupan tersimpan.

Gross sales dikurangi refunds menghasilkan net sales. Net profit mengurangi COGS serta variance expense. Laba ditahan neraca dihitung dari seluruh posting Revenue/Expense sampai tanggal akhir. Trial balance dan neraca berskala organisasi; P&L dapat memakai branch/periode WIB. UI menuliskan perbedaan ini agar filter tidak memberi kesan keliru.

## 10. Buat client online dan antrean offline yang berbeda

[api.ts](../frontend/src/api.ts) membungkus fetch, cookie, CSRF dan ProblemDetails. Ia memeriksa kegagalan jaringan dan tidak mengubah error menjadi data sukses. [types.ts](../frontend/src/types.ts) serta [retail-types.ts](../frontend/src/retail-types.ts) menghubungkan UI dengan bentuk data server.

[offline.ts](../frontend/src/offline.ts) menggunakan database IndexedDB `retailops-workstation-v1` dengan version schema yang terpisah dari nama database. `scopeFor` memakai organizationId/userId, `saveSnapshot`/`loadSnapshot` menangani katalog lokal, `enqueueSale` menulis perintah sebelum jaringan, dan `synchronizeSale` mengelola claim serta replay. `queuedOwnerHeaders` membentuk expected identity dari scope perintah yang sudah tersimpan.

`commandTotal`, `cashChange`, `sufficientCash` dan `isMoney` memakai batas serta integer cents ketika membandingkan uang. Ini mencegah masalah 0.1+0.2 pada perbandingan cash/total. Backend tetap menjadi pemeriksa akhir.

[retail-operations.ts](../frontend/src/retail-operations.ts) menyediakan `enqueueOperation`/`synchronizeOperation` untuk shift/close/return yang memerlukan pemulihan setelah dialog ditutup atau reload. Menekan Escape boleh menutup dialog, tetapi tidak boleh menghilangkan key perintah yang hasilnya belum pasti.

Jika request mungkin sudah commit, uncertainty bersifat lengket.403 setelah assignment dicabut tidak membuktikan request awal gagal. Perintah tetap dipertahankan sampai hasilnya direkonsiliasi. Penolakan definitif pada percobaan awal dapat ditinjau dan diarsipkan secara eksplisit.

Data IndexedDB tidak dienkripsi sebagai privasi antar pengguna perangkat. Partisi aplikasi mencegah UI/replay memakai identitas berbeda; pengguna yang menguasai profil browser/devtools tetap dapat membaca penyimpanan lokal. Password dan token tidak disimpan di dalamnya.

## 11. Hubungkan workspace dan production shell

[App.tsx](../frontend/src/App.tsx) mengatur setup/login, session context, route dan navigasi per role. Pergantian akun di tab lain menginvalidasi context/query lama dan meremount workspace berdasarkan identitas, sehingga cart/branch pengguna sebelumnya tidak terbawa.

[Retail.tsx](../frontend/src/pages/Retail.tsx) memuat komponen POS, sinkronisasi, shifts, receipts/returns, branches/pricing dan accounting. Komponen back-office yang diwarisi tetap pada file halaman masing-masing. [components.tsx](../frontend/src/components.tsx) menyediakan form, modal, focus handling dan state umum; [styles.css](../frontend/src/styles.css) memuat layout desktop/tablet/mobile.

[vite.config.ts](../frontend/vite.config.ts) menghasilkan `sw.js` dan daftar precache aset public hasil build. Worker tidak menyimpan respons API umum. Mode production dipakai saat membuktikan reload offline; `npm run dev` menyediakan HMR dan bukan jalur bukti tersebut.

## 12. Verifikasi alur, bukan hanya kompilasi

[Backend tests](../backend/RetailOps.Tests/) memakai database PostgreSQL UUID yang terisolasi untuk membuktikan role, locking, idempotensi, retur bersamaan dan constraint GL. [Browser tests](../frontend/tests/) memakai app hasil build, IndexedDB dan service worker sungguhan, termasuk request yang sudah commit tetapi responsnya hilang.

[Start.ps1](../scripts/Start.ps1) menjalankan stack lokal; [Stop.ps1](../scripts/Stop.ps1) mempertahankan volume. [seed-demo.py](../scripts/seed-demo.py) adalah tooling opsional Python untuk workspace latihan kosong, bukan backend kedua. Ia membuat credential lokal acak dan berhenti jika langkah non-idempoten mempunyai hasil yang belum jelas.

Baca [hasil verifikasi](VERIFICATION.md) untuk angka test dan keterbatasan yang benar-benar dicatat. Kode yang berhasil build belum otomatis membuktikan konflik stok, offline reload, otorisasi atau journal yang seimbang.
