# ADR-001: معماری Database-per-Tenant با SQLite WAL، کش حافظه Write-Through و پایپ‌لاین‌های ایزوله مبتنی بر Channel

## وضعیت (Status)
پذیرفته‌شده (Accepted)[cite: 1]

## زمینه و مسئله (Context)
در طول حراج‌های لحظه‌ای بلک فرایدی، چندین خرده‌فروش (Tenant) به صورت هم‌زمان پروموشن‌های پرتقاضایی را اجرا می‌کنند[cite: 1]. پلتفرم باید الزامات زیر را تضمین کند[cite: 1]:
۱. تحت هیچ شرایطی و زیر بار هزاران درخواست هم‌زمان، بیش از موجودی نفروشد (Zero Overselling)[cite: 1].
۲. حجم بالای درخواست‌های خواندن وضعیت حراج را با تأخیر (Latency) بسیار پایین و بدون تحمیل بار اضافی به دیتابیس پاسخ دهد[cite: 1].
۳. فرآیند تکمیل و نهایی‌سازی سفارش را خارج از جریان اصلی (Out-of-Band) از طریق درگاه سرویس‌های پایین‌دستی (`IDownstreamOrderGateway`) به همراه مکانیزم تلاش مجدد (Retry) و صف پیام‌های خطا (Dead-Letter Queue - DLQ) پردازش کند[cite: 1].
۴. ایزوله‌سازی کامل بین Tenantها را در لایه ذخیره‌سازی، کش و پایپ‌لاین‌های پردازش پس‌زمینه حفظ کرده و از ایجاد منابع ذخیره‌سازی برای مستأجرهای غیرمجاز جلوگیری کند[cite: 1].

## گزینه‌های بررسی‌شده (Options Considered)
۱. **یک دیتابیس SQLite مشترک در برابر فایل‌های دیتابیس SQLite مجزا به ازای هر Tenant مجاز (Database-per-Tenant)**[cite: 1]
- *فایل مشترک (Shared File):* یک فایل SQLite واحد در حین تراکنش‌های نوشتن، کل دیتابیس را قفل می‌کند؛ در نتیجه جهش ترافیک حراج در یک Tenant باعث مسدود شدن و کندی سایر Tenantها می‌شود[cite: 1].
- *دیتابیس مجزا به ازای هر Tenant (`{tenantId}.db`) همراه با لیست مجاز (`AllowedTenants`):* هر Tenant ثبت‌شده یک فایل دیتابیس SQLite کاملاً مجزا و ایزوله از نظر فیزیکی دارد که در حالت `WAL` (Write-Ahead Logging) اجرا شده و توسط یک قفل اختصاصی `SemaphoreSlim` به ازای همان Tenant مدیریت می‌شود[cite: 1]. درخواست‌های دارای شناسه مستأجر ثبت‌نشده پیش از لمس دیسک با کد `HTTP 403 Forbidden` مسدود می‌شوند تا از حملات پر کردن دیسک (Disk Exhaustion) جلوگیری شود.

۲. **خواندن مستقیم از دیتابیس در برابر `IMemoryCache` تفکیک‌شده بر اساس Tenant با به‌روزرسانی Write-Through**[cite: 1]
- *خواندن مستقیم از دیتابیس (Direct DB Reads):* ایجاد گلوگاه شدید I/O زیر بار ترافیک لحظه‌ای (Burst Traffic)[cite: 1].
- *کش `IMemoryCache` به صورت Write-Through:* پاسخ‌دهی به درخواست‌های خواندن از حافظه RAM در کمتر از `0.1ms` با استفاده از کلیدهای تفکیک‌شده با پیشوند `tenant:{tenantId}:sale:{saleId}`[cite: 1]. هر زمان که متد `ReserveAsync` موجودی را کسر می‌کند، موجودیت به‌روزشده `SaleEvent` بلافاصله در داخل همان قفلِ Tenant در کش بازنویسی می‌شود[cite: 1].

۳. **صف پس‌زمینه مشترک در برابر `System.Threading.Channels` مجزا به ازای هر Tenant**[cite: 1]
- *صف مشترک (Shared Queue):* جهش ناگهانی ۱۰,۰۰۰ درخواست رزرو روی Tenant A باعث تأخیر در تایید سفارش‌های Tenant B می‌شود (مشکل Head-of-Line Blocking)[cite: 1].
- *کانال اختصاصی `Channel<ReservationJob>` به ازای هر Tenant:* هر Tenant کانال و حلقه پردازشگر (Worker Loop) اختصاصی خودش را دارد که عملیات هماهنگی انبار و ارسال اعلان را از طریق انتزاع `IDownstreamOrderGateway` اجرا کرده و در صورت بروز خطا، پس از ۳ مرتبه تلاش مجدد با تأخیر نمایی (Exponential Backoff)، جاب را در جدول اختصاصی `DeadLetterJobs` ذخیره می‌کند تا بدون دست‌کاری داده‌های مشتری قابل بازپخش (Replay) باشد[cite: 1].

## تصمیم نهایی (Decision)
ما ترکیب **SQLite به صورت Database-per-Tenant (در حالت `WAL`) با اعتبارسنجی لیست مستأجرهای مجاز (`AllowedTenants`)**، **کش `IMemoryCache` به صورت Write-Through با پیشوند اختصاصی Tenant** و **پردازشگرهای پس‌زمینه `Channel<ReservationJob>` مجزا به ازای هر Tenant** را انتخاب کردیم[cite: 1].

## پیامدها و تریدآف‌های سازگاری خواندن (Consequences & Read Consistency Trade-offs)
- **جلوگیری کامل از بیش‌فروشی (Zero Overselling):** ترکیب سریال‌سازی نوشتن به ازای هر Tenant با دستور اتمیک و تک‌مرحله‌ای `UPDATE SaleEvents SET RemainingStock = RemainingStock - @qty WHERE RemainingStock >= @qty` و قید کنترلی `CHECK (RemainingStock >= 0)` در سطح دیتابیس SQLite، عدم رخداد هرگونه خطای هم‌زمانی (Race Condition) را تضمین می‌کند[cite: 1].
- **امنیت و حفاظت از منابع دیسک (Tenant Allowlisting):** الزام به تطابق هدر `X-Tenant-ID` با هر دو شرط الگوی Regex و لیست سفید `Storage:AllowedTenants` مانع از ایجاد فایل‌های `.db` ناخواسته برای درخواست‌های ناشناس می‌شود.
- **سازگاری خواندن در برابر کهنگی داده (Read Consistency vs. Staleness):** روی یک نود (Single Node)، به‌روزرسانی کش به روش Write-Through سازگاری قوی (Strong Consistency) را برای مقدار `RemainingStock` فراهم می‌کند و هم‌زمان تغییر وضعیت‌های زمانی (`Upcoming` -> `Active` -> `Expired`) در لحظه خواندن با مقایسه دینامیک در برابر `DateTime.UtcNow` محاسبه می‌شوند[cite: 1]. در صورت مقیاس‌پذیری افقی (Horizontal Scaling) روی چندین نمونه (Instance) از API، کشِ `IMemoryCache` روی نودهای همتا حداکثر تا سقف پنجره زمانی TTL (۱۰ ثانیه) می‌تواند دارای کهنگی داده (Staleness) باشد که در محیط کلاستر چندنودی با استفاده از Redis Pub/Sub برای ابطال کش (Cache Invalidation) برطرف می‌شود[cite: 1].