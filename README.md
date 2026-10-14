# موتور فروش لحظه‌ای بلک فرایدی با معماری Multi-Tenant (.NET 8 + SQLite)

## ۱. نمای کلی و معماری سیستم (Architecture Overview)
این پروژه یک سرویس رزرو فروش لحظه‌ای (Flash Sale Reservation Engine) چندمستأجره (Multi-Tenant) است که با استفاده از **.NET 8 Minimal APIs** و **معماری Database-per-Tenant مبتنی بر SQLite در حالت WAL** طراحی و پیاده‌سازی شده است تا در اوج ترافیک بلک فرایدی از هرگونه بیش‌فروشی (Overselling) و تأثیرگذاری بار یک مستأجر بر مستأجر دیگر جلوگیری کند[cite: 1].

```text
                       +-------------------------------------------+
                       |     HTTP Request (X-Tenant-ID Header)     |
                       |   [Regex Validation + AllowedTenants]     |
                       +---------------------+---------------------+
                                             |
                  +--------------------------+--------------------------+
                  |                                                     |
        [GET /api/sales/{id}]                           [POST /api/sales/{id}/reservations]
                  |                                                     |
                  v                                                     v
    +---------------------------+                         +---------------------------+
    |  Tenant-Namespaced Cache  | <--- Write-Through ---- | Per-Tenant SemaphoreSlim  |
    |  (IMemoryCache, TTL 10s)  |      Stock Update       |   + Atomic SQL Decrement  |
    +-------------+-------------+                         +-------------+-------------+
                  | (Cache Miss)                                        |
                  +--------------------------+--------------------------+
                                             |
                                             v
                     +-----------------------------------------------+
                     |  Isolated SQLite DB File ({tenantId}.db WAL)  |
                     +-----------------------+-----------------------+
                                             |
                                    (202 Accepted)
                                             |
                                             v
                     +-----------------------------------------------+
                     | Isolated Channel<ReservationJob> per Tenant   |
                     |   -> IDownstreamOrderGateway (Sync & Notify)  |
                     |   -> 3x Retry (Exponential Backoff)           |
                     |   -> Order Persistence & Persistent DLQ       |
                     +-----------------------------------------------+
```

### ویژگی‌های کلیدی معماری
- **ایزوله‌سازی فیزیکی ذخیره‌سازی و کنترل دسترسی Tenantها:** داده‌های هر خرده‌فروش مجاز در یک فایل دیتابیس SQLite کاملاً مجزا (`data/tenant-electronics.db` و `data/tenant-fashion.db`) در حالت `WAL` (Write-Ahead Logging) ذخیره می‌شود تا قفل‌های نوشتن یا خطاهای یک Tenant هیچ اثری بر سایر Tenantها نداشته باشد[cite: 1]. همچنین، `TenantDbManager` تنها به مستأجرهای ثبت‌شده در لیست مجاز (`Storage:AllowedTenants`) اجازه دسترسی می‌دهد و درخواست‌های مستأجرهای ناشناس را با کد `HTTP 403 Forbidden` رد می‌کند تا از ساخته‌شدن فایل‌های دیتابیس ناخواسته روی دیسک جلوگیری شود.
- **رزرو اتمیک موجودی (Zero Overselling):** ترکیب قفل اختصاصی `SemaphoreSlim` به ازای هر Tenant با دستور اتمیک تک‌مرحله‌ای `UPDATE SaleEvents SET RemainingStock = RemainingStock - @qty WHERE RemainingStock >= @qty` و قید کنترلی `CHECK (RemainingStock >= 0)` در سطح دیتابیس، تضمین می‌کند که تحت هیچ شرایطی خطای هم‌زمانی (Race Condition) یا فروش بیش از سقف موجودی رخ ندهد[cite: 1].
- **عملکرد خواندن سریع زیر بار شدید (Burst Read Strategy):** بهره‌گیری از `IMemoryCache` تفکیک‌شده بر اساس کلیدهای اختصاصی هر مستأجر (`tenant:{tenantId}:sale:{saleId}`) همراه با به‌روزرسانی آنی (Write-Through) بلافاصله پس از کسر موجودی در داخل قفلِ نوشتن[cite: 1].
- **پایپ‌لاین پردازش سفارش خارج از جریان اصلی (Out-of-Band) با قابلیت بازیابی:** هر Tenant یک صف غیرمسدودکننده `Channel<ReservationJob>` و پردازشگر پس‌زمینه اختصاصی دارد که از طریق سرویس `IDownstreamOrderGateway` مراحل همگام‌سازی انبار، ثبت سفارش قطعی و ارسال اعلان را با **۳ مرتبه تلاش مجدد (Exponential Backoff: 150ms, 300ms, 600ms)**، **صف ماندگار پیام‌های خطا (DLQ)** و **بازیابی خودکار رزروهای ناتمام در زمان راه‌اندازی (Startup Recovery)** انجام می‌دهد[cite: 1].

---

## ۲. راهنمای راه‌اندازی سریع (Quick Start)

کل سیستم بدون نیاز به هیچ‌گونه تنظیم دستی دیتابیس، تنها با یک دستور راه‌اندازی شده و داده‌های اولیه (Seed Data) به صورت خودکار، قطعی (Deterministic) و تکرارپذیر (Idempotent) برای دو مستأجر `tenant-electronics` و `tenant-fashion` (شامل ۱۰ کالای متنوع در وضعیت‌های فعال، در پیش‌رو و منقضی‌شده) ایجاد می‌شوند[cite: 1]. در صورت ری‌استارت شدن کانتینر پس از چند ساعت، بازه‌های زمانی نسبی رویدادهای Seed به صورت خودکار به‌روزرسانی می‌شوند تا همواره آماده تست باشند:

```bash
docker compose up --build -d
```

- **رابط کاربری Swagger/OpenAPI:** `http://localhost:8080/swagger`[cite: 1]
- **بررسی سلامت سرویس (Health Check):** `http://localhost:8080/health`[cite: 1]

---

## ۳. راهنمای تست API (نمونه دستورات `curl`)

تمامی درخواست‌ها به مسیر `/api/*` نیازمند ارسال هدر معتبر و ثبت‌شده `X-Tenant-ID` (مانند `tenant-electronics` یا `tenant-fashion`) هستند[cite: 1].

### الف) مشاهده لیست تمام حراج‌های یک Tenant
```bash
curl -s -H "X-Tenant-ID: tenant-electronics" http://localhost:8080/api/sales | jq
```

### ب) استعلام وضعیت لحظه‌ای یک حراج (پاسخ‌دهی از کش با تأخیر زیر میلی‌ثانیه)
```bash
curl -s -H "X-Tenant-ID: tenant-electronics" \
  http://localhost:8080/api/sales/sale-elec-tv65 | jq
```

### ج) ایجاد یک رویداد حراج جدید برای Tenant
```bash
curl -i -X POST http://localhost:8080/api/sales \
  -H "X-Tenant-ID: tenant-electronics" \
  -H "Content-Type: application/json" \
  -d '{
    "id": "sale-elec-ipadpro",
    "skuId": "SKU-ELEC-006",
    "productName": "iPad Pro 13-inch M4",
    "originalPrice": 1299.00,
    "discountedPrice": 999.00,
    "initialStock": 60,
    "startTimeUtc": "2025-01-01T00:00:00Z",
    "endTimeUtc": "2027-01-01T00:00:00Z"
  }'
```

### د) ثبت درخواست رزرو کالا (Atomic Stock Reservation)
```bash
curl -i -X POST http://localhost:8080/api/sales/sale-elec-tv65/reservations \
  -H "X-Tenant-ID: tenant-electronics" \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "cust-101",
    "quantity": 1,
    "simulateDownstreamFailure": false
  }'
```

### هـ) پیگیری وضعیت یک رزرو خاص با شناسه رزرو
```bash
curl -s -H "X-Tenant-ID: tenant-electronics" \
  http://localhost:8080/api/reservations/{reservationId} | jq
```

### و) شبیه‌سازی خطا در پایپ‌لاین پس‌زمینه و بررسی انتقال به صف خطا (DLQ)
```bash
# ۱. ارسال درخواست رزرو با فعال‌سازی فلگ شبیه‌ساز خطای سرویس پایین‌دستی (IDownstreamOrderGateway)
curl -s -X POST http://localhost:8080/api/sales/sale-elec-ps5pro/reservations \
  -H "X-Tenant-ID: tenant-electronics" \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "cust-dlq-test-99",
    "quantity": 1,
    "simulateDownstreamFailure": true
  }'

# ۲. مشاهده وضعیت پایپ‌لاین، تعداد تلاش‌های مجدد (Retries) و لیست آیتم‌های DLQ
curl -s -H "X-Tenant-ID: tenant-electronics" \
  http://localhost:8080/api/pipeline/status | jq

# ۳. بازپخش (Replay) جاب شکست‌خورده از صف DLQ بدون تغییر در اطلاعات مشتری
curl -i -X POST http://localhost:8080/api/pipeline/dlq/{dlqId}/replay \
  -H "X-Tenant-ID: tenant-electronics"
```

---

## ۴. تست هم‌زمانی و شواهد عدم بیش‌فروشی (Concurrency Verification)

این پروژه دارای دو روش مکمل برای اثبات صحت عملکرد در بار هم‌زمان است:

### روش اول: اجرای تست‌های یکپارچه و هم‌زمانی خودکار با xUnit (`dotnet test`)
پروژه `tests/BlackFriday.Tests` با استفاده از `WebApplicationFactory`، سناریوی شلیک هم‌زمان ۱۵۰ درخواست رزرو به یک حراج با موجودی ۱۰۰ عدد و همچنین مسدودسازی مستأجرهای غیرمجاز (`403 Forbidden`) را به صورت خودکار آزمایش می‌کند:

```bash
dotnet test BlackFriday.sln
```

### روش دوم: اسکریپت تست بار هم‌زمان (`tests/test-concurrency.sh`)
اسکریپت `tests/test-concurrency.sh` در هر بار اجرا ابتدا یک رویداد حراج تازه با **۱۰۰ عدد موجودی اولیه** ایجاد کرده و سپس **۱۵۰ درخواست رزرو هم‌زمان** را به صورت موازی به سمت آن شلیک می‌کند (بنابراین بدون نیاز به پاک کردن Volume داکر، به دفعات قابل تکرار است)[cite: 1]:

```bash
chmod +x tests/test-concurrency.sh
./tests/test-concurrency.sh
```

#### خروجی مورد انتظار:
- دقیقاً **۱۰۰ درخواست** کد وضعیت `HTTP 202 Accepted` دریافت می‌کنند[cite: 1].
- دقیقاً **۵۰ درخواست** کد وضعیت `HTTP 409 Conflict` (اتمام موجودی) دریافت می‌کنند[cite: 1].
- موجودی نهایی (`remainingStock`) دقیقاً برابر `0` شده و ۱۰۰ سفارش در پایپ‌لاین پس‌زمینه ثبت می‌گردد (**Zero Overselling**)[cite: 1].

---

## ۵. فرضیات طراحی (Assumptions)

۱. **شناسایی و اعتبارسنجی مستأجر (Tenant Resolution & Allowlisting):** فرض شده است که شناسه مستأجر از طریق هدر استاندارد `X-Tenant-ID` به سرویس ارسال می‌شود[cite: 1]. برای جلوگیری از حملات Path Traversal و جلوگیری از ایجاد فایل‌های دیتابیس جعلی روی دیسک، مقدار این هدر ابتدا با الگوی عبارات باقاعده (`^[a-zA-Z0-9_-]{3,50}$`) بررسی شده[cite: 1] و سپس با لیست مستأجرهای مجاز (`Storage:AllowedTenants` در `appsettings.json`) تطبیق داده می‌شود.
۲. **رزرو فوری موجودی در لحظه پذیرش:** موجودی کالا در لحظه ثبت رزرو (`POST /reservations`) به صورت قطعی کسر می‌شود تا پاسخ مشتری آنی باشد و پردازش‌های زمان‌بر (هماهنگی انبار، ثبت نهایی سفارش و ارسال نوتیفیکیشن) از طریق `IDownstreamOrderGateway` به صورت غیرهمگام در پس‌زمینه انجام شوند[cite: 1].
۳. **تراکنش جبرانی برای خطاهای پایپ‌لاین:** زمانی که یک رزرو پس از ۳ بار تلاش مجدد به صف DLQ منتقل می‌شود، موجودی کسرشده به صورت خودکار به حراج بازگردانده نمی‌شود تا تیم عملیات بتواند از طریق Endpoint بازپخش (`/api/pipeline/dlq/{dlqId}/replay`) سفارش مشتری را با حفظ کامل اطلاعات اولیه مشتری و بدون خطر اتمام موجودی نهایی کند[cite: 1].
۴. **مدیریت زمان به صورت UTC:** تمامی زمان‌های شروع و پایان حراج به صورت استاندارد `UTC` ذخیره و مقایسه می‌شوند تا اختلاف منطقه زمانی کلاینت‌ها باعث خطا در تشخیص وضعیت حراج نشود[cite: 1].

---

## ۶. محدودیت‌های شناخته‌شده و برنامه‌های توسعه آینده (Known Limitations & Future Work)

در صورتی که زمان بیشتری در اختیار بود و سیستم برای محیط عملیاتی چندنودی (Multi-Node Production Cluster) مقیاس‌دهی می‌شد، بهبودهای زیر اعمال می‌گردید[cite: 1]:

۱. **استفاده از قفل توزیع‌شده Redis (Redis Distributed Lock / Redlock) و کش توزیع‌شده:**
- در معماری فعلی، استفاده از `SemaphoreSlim` و `IMemoryCache` هماهنگی کامل را روی **یک نود (Single Container Instance)** تضمین می‌کند[cite: 1].
- برای مقیاس‌پذیری افقی (Horizontal Scaling) روی چندین نمونه هم‌زمان از API، قفل درون‌حافظه‌ای `SemaphoreSlim` باید با **قفل توزیع‌شده مبتنی بر Redis (الگوریتم Redlock با کلید `lock:tenant:{tenantId}:sale:{saleId}`)** یا عملیات اتمیک Lua Script در Redis جایگزین شود تا هم‌زمانی بین نودهای مختلف مدیریت گردد[cite: 1]. همچنین استفاده از **Redis Distributed Cache** به همراه **Redis Pub/Sub** برای ابطال آنی کش (Cache Invalidation) در تمامی نودها، پنجره کهنگی داده (۱۰ ثانیه TTL) را در محیط کلاستر به صفر می‌رساند[cite: 1].

۲. **پیاده‌سازی الگوی Transactional Outbox و پیام‌رسان خارجی (RabbitMQ / Kafka):**
- هرچند این سرویس در زمان Startup رزروهای در وضعیت `Reserved` را از دیتابیس بازیابی و مجدداً وارد `Channel<ReservationJob>` می‌کند، اما در مقیاس عملیاتی استفاده از جدول **Transactional Outbox** به همراه پیام‌رسان پایدار (مانند RabbitMQ یا Apache Kafka با تفکیک Topic/Routing Key به ازای هر Tenant) پایداری و توزیع بار پردازشگرهای پس‌زمینه را به شکل کامل تضمین می‌کند[cite: 1].

۳. **کلید یکتایی درخواست (Idempotency Key):**
- افزودن هدر `X-Idempotency-Key` به درخواست رزرو تا در صورت قطع موقت شبکه و ارسال مجدد درخواست توسط کلاینت، از ثبت رزرو تکراری برای یک مشتری جلوگیری شود[cite: 1].

۴. **محدودکننده نرخ درخواست به ازای هر مستأجر (Per-Tenant Rate Limiting):**
- افزودن Middleware اختصاصی `RateLimiter` در .NET 8 به صورت Partitioned بر اساس `X-Tenant-ID` و `CustomerId` جهت محافظت در برابر بات‌ها در ثانیه‌های آغازین حراج[cite: 1].