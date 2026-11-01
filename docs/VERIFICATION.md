# Bukti verifikasi

Verifikasi lokal pada 1 November 2026 memakai PostgreSQL 18.6, SDK .NET 10.0.403, Node.js22 dan Microsoft Edge headless. Database bisnis, database per-test, dan database browser terpisah. Semua data yang dimasukkan oleh pengujian bersifat sintetis.

## Hasil yang dijalankan

| Pemeriksaan | Hasil |
|---|---|
| Restore .NET dengan lockfile, build Release | Lulus |
| Backend dengan PostgreSQL nyata | **76/76 lulus**, 0 gagal, 0 skip |
| Frontend Vitest/jsdom | **35/35 lulus**, 6 file, 119,43 detik pada run penuh lokal; setelah penambahan regresi reconnect, **7/7 tes App** lulus dan typecheck/build diulang |
| TypeScript dan build frontend production | Lulus; shell service worker ikut dihasilkan |
| Browser production shell dengan server/IndexedDB/service worker nyata | Sembilan kasus lulus dalam run penuh; satu kasus respons hilang lulus pada rerun terfokus setelah koreksi sinkronisasi harness |
| Build image Docker dan startup Compose | Lulus; app8150 dan PostgreSQL5450 sehat |
| Konfigurasi Compose lokal dan overlay production | Validasi konfigurasi lulus |
| Seeder workspace kosong | Lulus: 3 cabang, 6 produk, PO disetujui akun berbeda, 3 penjualan dan retur parsial; jurnal/neraca seimbang |
| Initializer saat credential hilang tetapi volume masih ada | Menolak mengganti credential; tidak membuat `.env` baru |
| Audit dependency npm runtime | 0 vulnerability dilaporkan pada dependency production |

TRX backend lokal mempunyai SHA-256 `e924fd5892225eb0d33570708c42a4f5b657a6f8c8f880be57a63f15f5ee2995`. Durasi run final backend 648,655 detik pada komputer Windows ini; angka tersebut bukan benchmark layanan.

Tes backend terdiri dari authentication8, business10, consistency3, retail access6, retail accounting22, retail mutation14, queued identity3 dan security10. Setiap kasus membuat database `retailops_test_<UUID>` sendiri; setelah run final tidak ada database per-case yang tertinggal.

## Apa yang dibuktikan

- Penjualan/replay dan retur/replay menjaga satu efek stok, journal dan struk; payload/key yang dipakai ulang dengan isi berbeda ditolak.
- Dua penjualan berebut unit terakhir, retur bersamaan, perubahan cost dan mutasi stok memakai transaksi/penguncian PostgreSQL nyata.
- PostgreSQL menolak journal kosong/tidak seimbang, line nonpositif, perubahan/penghapusan/truncate, penambahan line pada header committed, dan source posting ganda. Tes juga melewati EF dengan SQL langsung.
- Return setelah stok nol dan perubahan standard cost mempertahankan COGS asli serta mencatat selisih inventory pada variance.
- Laporan dan open-shift expected cash memakai snapshot baca konsisten. P&L, trial balance dan persamaan neraca direkonsiliasi dengan posting.
- Cashier dibatasi assignment/own shift/own receipt dan tidak dapat membaca cost, supplier atau laporan pusat. Expected-owner header diperiksa terhadap sesi saat mutation diterima, termasuk replay.
- Browser benar-benar di-offline-kan, direload lewat service worker, lalu membuat command IndexedDB sebelum POST. Sinkronisasi menerima satu struk server tanpa mengganti payload/key asli.
- Respons dari sale yang sudah commit sengaja diputus pada lapisan browser; retry mengembalikan struk yang sama, satu sale, satu stock effect dan satu journal.
- Logout memperingatkan antrean belum selesai; akun lain tidak melihat atau mengirimnya. Dua tab dapat berebut command tanpa membuat penjualan ganda. Pergantian akun di antara pemeriksaan owner dan POST tetap ditolak melalui CSRF retry.
- Konflik harga yang definitif membutuhkan review, sedangkan hasil yang belum pasti tidak boleh dibuang hanya karena percobaan berikutnya mendapat penolakan akses.
- Alur pembelian, partial receipt, stock adjustment/transfer, staff access, pencarian/filter dan form error turut dijalankan melalui browser. POS/receipt/accounting diperiksa pada320,390 dan1440 piksel; menu/dialog dapat dibuka dengan keyboard dan ditutup dengan Escape.

## Koreksi yang ditemukan selama verifikasi

Label form bersama semula memasukkan seluruh pilihan select ke nama aksesibel. Komponen Field kini menghubungkan teks label saja ke control, dengan tes regresi. Pergantian akun pada tab lain juga semula tidak memulihkan tab yang sudah berada di login; session revalidation sekarang bekerja dari keadaan tersebut, tanpa mempercayai pointer lokal sebagai autentikasi. Event koneksi kembali kini memeriksa kedua query autentikasi agar status offline lama tidak tertinggal setelah server kembali dapat dihubungi; regresi juga memeriksa bahwa identitas server menggantikan identitas cache yang berbeda.

Dua koreksi harness tidak mengubah aplikasi: GET tidak lagi menghabiskan batas route intersepsi POST, dan tes respons hilang menunggu server menerima serta browser kehilangan respons sebelum membaca uncertainty. Pemeriksaan key/payload, stock, receipt dan journal tetap dipertahankan. Run gagal awal disimpan sebagai diagnostik lokal dan tidak dilabeli sebagai hasil lulus.

CI yang lebih cepat juga menemukan precondition navigasi: shell sudah menampilkan tombol keluar sebelum redirect Cashier dari root selesai. Helper login sekarang menunggu URL/heading tujuan yang sah sebelum tindakan berikutnya. Login fixture hanya boleh mengulang HTTP429 satu kali setelah Retry-After atau satu window61 detik; status lain tetap gagal. Batas autentikasi aplikasi **20 percobaan/IP/menit tetap berlaku**, tanpa mode bypass khusus CI. Artifact kegagalan hanya menambahkan konteks halaman sintetis untuk diagnosis.

## Review UI pada build akhir

Tujuh [screenshot aplikasi](../README.md) diambil dari Docker8150 dan ditinjau langsung. Hero1440x1250 memperlihatkan katalog, keranjang serta checkout; gambar lain menunjukkan antrean offline nyata sebelum sinkronisasi, struk server, laporan, jurnal, assignment cabang dan cart390px. Pengambilan ulang memakai hasil command yang sama, tanpa membuat penjualan dokumentasi berulang.

Capture akhir lulus dengan **0 page error, 0 console error, 30 sampel kontras**; minimum teks5,7083:1 dan control boundary3,5816:1. Font-size root16px diperbesar menjadi32px pada POS, harga dan accounting; ketiga view tidak overflow atau memiliki clipped text/control di bawah44px. Ini pemeriksaan text resize200%, bukan full browser zoom atau klaim seluruh WCAG telah diaudit.

Probe terpisah melakukan offline reload dengan profile baru, kemudian native online event memicu pembacaan status/current user dari server dan menghilangkan banner offline sebelum tindakan autentikasi manual. Katalog tetap dapat dibaca; jumlah command lokal dan struk server tidak berubah, dengan **0 retail write dan 0 page/console error**. Browser CI juga mempunyai assertion pemulihan banner pada kasus offline yang sama.

Bundle JavaScript yang dilayani Docker adalah `index-lE5byniC.js`, 415.944 byte, SHA-256 `23f1322873b806ca83c47e5e9bcbfc1c1ffe2007127bd8e00b0a4b218cd24744`; byte cocok dengan frontend build lokal. Precache hanya memuat shell/font/JS/CSS public, tanpa respons API. Empat blok review root tercatat di [Delivery Gate](DELIVERY-GATE.md).

## Mengulang pemeriksaan

Gunakan [scripts/test.ps1](../scripts/test.ps1) untuk tes backend dan unit frontend setelah konfigurasi lokal tersedia; [scripts/build.ps1](../scripts/build.ps1) membangun aplikasi. Browser membutuhkan app production hasil build dan PostgreSQL sintetis terisolasi, bukan database kerja. [Workflow CI](../.github/workflows/ci.yml) mendefinisikan urutan restore terkunci, build, 76 tes backend, unit frontend, build production, browser dengan nol skip, pemeriksaan role database runtime dan build Docker.

Log, TRX lokal, credential serta profile browser disimpan di `.local` yang diabaikan Git. CI hanya mengunggah bukti terpilih dengan masa simpan terbatas; tidak mengunggah `.env`, profile, trace, database atau log server lengkap.

## Batas bukti

Ini demonstrasi bisnis kas menggunakan data sintetis. Pengujian ini bukan load test, audit keamanan, sertifikasi akuntansi/pajak atau bukti pemakaian pelanggan. Database role lokal default adalah pemilik migration; deployment production memisahkan role runtime. Pemilik database yang mempunyai hak DDL tetap dapat mengubah schema/trigger di luar perimeter aplikasi.

Konfigurasi HTTPS/domain production tersedia, tetapi DNS/sertifikat host publik dan pemulihan backup operator belum dijalankan. IndexedDB terpartisi oleh aplikasi dan tidak memberikan enkripsi/privasi terhadap orang yang menguasai profile browser. Penghapusan browser storage dapat menghilangkan command lokal. Tidak ada payment gateway, pajak atau pelunasan utang supplier otomatis.
