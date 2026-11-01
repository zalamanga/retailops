# REST API

API memakai satu origin dengan UI, cookie HttpOnly dan header CSRF. Endpoint serta bentuk retail yang disepakati tersedia pada [kontrak implementasi](RETAIL-CONTRACT.md). Development menyediakan OpenAPI pada `/openapi/v1.json`; dokumen generated ini mengikuti endpoint yang dikompilasi.

## Persiapan permintaan

1. Baca `/api/auth/csrf`, simpan cookie dan token respons dalam sesi HTTP.
2. POST JSON ke `/api/auth/setup` untuk workspace kosong, atau `/api/auth/login`.
3. Ambil CSRF baru setelah identitas berubah.
4. Sertakan `X-CSRF-TOKEN` pada semua mutation. Untuk operasi sale/return/shift gunakan juga `Idempotency-Key` UUID.

Antrean browser menyertakan pasangan `X-Retail-Actor` dan `X-Retail-Organization` dari pemilik perintah yang tersimpan. Server memeriksa keduanya terhadap sesi saat mutation diproses, termasuk sesudah retry CSRF.403 dengan judul `Queued identity changed` berarti perintah tidak boleh diproses sebagai pengguna lain. Client API biasa yang tidak memakai antrean dapat memakai sesi saat ini tanpa pasangan header opsional tersebut.

Jangan menyalin cookie, password atau token ke README, screenshot, query string maupun log. Body sale menyebut product, quantity, unitPrice dan priceVersion; total serta cost dihitung server.

## Kelompok endpoint

| Kelompok | Route utama | Akses |
|---|---|---|
| Identitas | `/api/auth/status`, `/api/auth/csrf`, `/api/auth/setup`, `/api/auth/login`, `/api/auth/me`, `/api/auth/logout` | Setup/login sesuai lifecycle; sesi untuk me/logout |
| Purchasing | `/api/orders`, `/api/products`, `/api/suppliers`, `/api/warehouses`, `/api/inventory` | Role back-office yang diwarisi |
| Cabang | `/api/retail/branches`, `/api/retail/assignments` | Daftar scoped; konfigurasi Admin |
| Harga/POS | `/api/retail/prices`, `/api/retail/catalog?branchId=...` | Konfigurasi Admin; katalog scoped |
| Shift | `/api/retail/shifts`, `/api/retail/shifts/{id}/close` | Shift sendiri dalam cabang yang diizinkan |
| Sale/return | `/api/retail/sales`, `/api/retail/sales/{id}`, `/api/retail/sales/{id}/returns` | Sale scoped; retur Manager/Admin |
| Akuntansi | `/api/retail/journals`, `/api/retail/reports` | Manager/Admin |

## Status respons

400 menandakan format/validasi yang tidak dapat diterima; CSRF mempunyai judul ProblemDetails tersendiri dan memerlukan token baru.401 berarti autentikasi perlu dipulihkan.403 berarti akses tidak diizinkan.404 menyembunyikan resource yang tidak dapat dibaca.409 dipakai untuk konflik state, harga, stok atau identitas request. Error server/jaringan dapat mempunyai hasil yang belum pasti, sehingga key perintah tetap dipertahankan.

Antrean tidak boleh menganggap timeout sebagai penolakan definitif, dan tidak boleh membuat key baru hanya agar retry berhasil. Reuse key dengan payload berbeda ditolak. Data yang sensitif terhadap branch tetap diperiksa ulang ketika replay.
