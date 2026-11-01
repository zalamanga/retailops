# Kebutuhan produk

RetailOps membantu kasir cabang dan tim pusat mencatat penjualan tanpa mengorbankan konsistensi stok serta jurnal. Pengguna dapat melanjutkan pencatatan saat koneksi terputus, dengan perbedaan yang jelas antara perintah lokal dan penjualan yang sudah diterima server.

## Peran

Admin mengelola organisasi, akun, cabang, assignment dan harga. Manager bekerja sebagai pengawas pusat: persetujuan PO, pelaporan dan retur. Operator/Viewer mempertahankan kewenangan back-office dari fondasi pembelian. Cashier dibatasi ke cabang yang ditugaskan, shift miliknya, katalog POS dan struk penjualannya. Pembatasan berlaku pada API, termasuk ketika UI dilewati.

## Kriteria penerimaan

- Penjualan terakhir dari dua kasir yang bersamaan menghasilkan paling banyak satu keberhasilan jika stok tinggal satu.
- Sale, stock movement, audit dan journal commit bersama; kegagalan tidak boleh menyisakan sebagian efek.
- Key yang sama dengan payload sama mengembalikan hasil yang sama. Pemakaian ulang dengan payload berbeda ditolak.
- Harga lokal yang kedaluwarsa tidak diganti diam-diam oleh server atau antrean.
- Cashier tidak dapat membaca cabang atau antrean identitas lain, dan tidak dapat membuka route pembelian/akuntansi pusat.
- App shell yang sudah dimuat dapat dibuka ulang tanpa koneksi. Perintah tersimpan sebelum pengiriman dan bertahan setelah reload.
- Perintah dengan hasil belum pasti mempertahankan identitas. Konflik definitif dapat dibuang atau dibuat ulang setelah pengguna meninjau perubahan.
- Retur paralel tidak dapat melampaui jumlah terjual; reversal memakai harga dan COGS penjualan asli.
- Journal append-only dan debit/kredit diperiksa juga oleh PostgreSQL pada commit.
- Angka laporan berasal dari journal yang sudah commit, dengan cakupan periode/cabang yang dijelaskan.
- Setiap kontrol mempunyai aksi; loading, empty, offline, pending, conflict dan failure terbaca di desktop serta ponsel.

## Batas produk

Satu organisasi dengan beberapa cabang/gudang. Kas dicatat operator; tidak ada integrasi pembayaran provider, perpajakan, payroll atau otorisasi bank. Harga memakai IDR dengan paling banyak dua desimal dan stok satuan utuh. Supplier payment settlement dan invoice legal tidak termasuk. Neraca organisasi bukan neraca legal perusahaan yang telah diaudit. Offline tidak menjamin semua transaksi diterima saat sinkronisasi; stok/harga/akses server tetap menentukan.
