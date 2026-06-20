using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Newtonsoft.Json;
using System.Text;

// ==================== CONFIG ====================
const string BOT_TOKEN = "YOUR_BOT_TOKEN_HERE";
const string API_URL = "https://shahrint.com/api/business/set";
const long ADMIN_TELEGRAM_ID = 123456789; // آیدی تلگرام خودت
const string OPERATORS_FILE = "operators.json";
// ================================================

var bot = new TelegramBotClient(BOT_TOKEN);
var sessions = new Dictionary<long, RegistrationSession>();
var httpClient = new HttpClient();

// لود اپراتورها از فایل
var operators = LoadOperators();

Console.WriteLine("Bot started...");

bot.StartReceiving(
    HandleUpdateAsync,
    HandleErrorAsync,
    new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] }
);

await Task.Delay(Timeout.Infinite);

// ===================== OPERATOR MANAGEMENT =====================

HashSet<long> LoadOperators()
{
    if (!File.Exists(OPERATORS_FILE)) return new HashSet<long>();
    var json = File.ReadAllText(OPERATORS_FILE);
    return JsonConvert.DeserializeObject<HashSet<long>>(json) ?? new HashSet<long>();
}

void SaveOperators()
{
    File.WriteAllText(OPERATORS_FILE, JsonConvert.SerializeObject(operators));
}

bool IsOperator(long id) => id == ADMIN_TELEGRAM_ID || operators.Contains(id);
bool IsAdmin(long id) => id == ADMIN_TELEGRAM_ID;

// ===================== HANDLERS =====================

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
{
    try
    {
        if (update.Message is { } msg)
            await HandleMessage(botClient, msg, ct);
        else if (update.CallbackQuery is { } cb)
            await HandleCallback(botClient, cb, ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
{
    Console.WriteLine($"Bot error: {exception.Message}");
    return Task.CompletedTask;
}

async Task HandleMessage(ITelegramBotClient bot, Message msg, CancellationToken ct)
{
    var chatId = msg.Chat.Id;
    var userId = msg.From!.Id;
    var text = msg.Text?.Trim() ?? "";

    // دستورات ادمین
    if (IsAdmin(userId))
    {
        if (text.StartsWith("/addop "))
        {
            if (long.TryParse(text[7..], out long opId))
            {
                operators.Add(opId);
                SaveOperators();
                await bot.SendMessage(chatId, $"✅ اپراتور {opId} اضافه شد.", cancellationToken: ct);
            }
            else await bot.SendMessage(chatId, "❌ آیدی نامعتبر.", cancellationToken: ct);
            return;
        }
        if (text.StartsWith("/removeop "))
        {
            if (long.TryParse(text[10..], out long opId))
            {
                operators.Remove(opId);
                SaveOperators();
                await bot.SendMessage(chatId, $"✅ اپراتور {opId} حذف شد.", cancellationToken: ct);
            }
            else await bot.SendMessage(chatId, "❌ آیدی نامعتبر.", cancellationToken: ct);
            return;
        }
        if (text == "/listop")
        {
            var list = operators.Count > 0 ? string.Join("\n", operators) : "لیست خالیه";
            await bot.SendMessage(chatId, $"👥 اپراتورها:\n{list}", cancellationToken: ct);
            return;
        }
    }

    // شروع
    if (text == "/start")
    {
        sessions.Remove(chatId);
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("➕ ثبت کسب‌وکار جدید", "start_register") }
        });
        string welcome = IsOperator(userId)
            ? "👋 خوش آمدید اپراتور عزیز!"
            : "👋 خوش آمدید!\nبرای ثبت کسب‌وکار خود اقدام کنید.";
        await bot.SendMessage(chatId, welcome, replyMarkup: keyboard, cancellationToken: ct);
        return;
    }

    if (!sessions.TryGetValue(chatId, out var session)) return;
    session.IsOperator = IsOperator(userId);

    // دریافت تصویر
    if (msg.Photo != null || msg.Document != null)
    {
        await HandlePhotoStep(bot, msg, session, chatId, ct);
        return;
    }

    if (string.IsNullOrEmpty(text)) return;

    switch (session.Step)
    {
        case Step.StoreName:
            session.Data.StoreName = text;
            session.Step = Step.Owner;
            await bot.SendMessage(chatId, "👤 نام مالک:", cancellationToken: ct);
            break;

        case Step.Owner:
            session.Data.Owner = text;
            if (session.IsOperator)
            {
                session.Step = Step.CustomerId;
                await bot.SendMessage(chatId, "🆔 کد کاربری مشتری (customerid):", cancellationToken: ct);
            }
            else
            {
                session.Data.CustomerId = (int)userId;
                session.Step = Step.CatId;
                await bot.SendMessage(chatId, "📂 کد دسته‌بندی:\nمثال: 1174", cancellationToken: ct);
            }
            break;

        case Step.CustomerId:
            if (!int.TryParse(text, out int cid)) { await bot.SendMessage(chatId, "❌ عدد وارد کنید.", cancellationToken: ct); return; }
            session.Data.CustomerId = cid;
            session.Step = Step.CatId;
            await bot.SendMessage(chatId, "📂 کد دسته‌بندی:", cancellationToken: ct);
            break;

        case Step.CatId:
            if (!int.TryParse(text, out int catId)) { await bot.SendMessage(chatId, "❌ عدد وارد کنید.", cancellationToken: ct); return; }
            session.Data.CatId = catId;
            session.Step = Step.City;
            await bot.SendMessage(chatId,
                "🏙 کد شهر:\n11 مازندران\n21 تهران\n25 قم\n51 خراسان رضوی\n66 لرستان",
                cancellationToken: ct);
            break;

        case Step.City:
            if (!int.TryParse(text, out int city)) { await bot.SendMessage(chatId, "❌ عدد وارد کنید.", cancellationToken: ct); return; }
            session.Data.City = city;
            session.Step = Step.Region;
            await bot.SendMessage(chatId, "🗺 کد منطقه:", cancellationToken: ct);
            break;

        case Step.Region:
            session.Data.Region = text;
            session.Step = Step.Address;
            await bot.SendMessage(chatId, "📍 آدرس کامل:", cancellationToken: ct);
            break;

        case Step.Address:
            session.Data.Address = text;
            session.Step = Step.LocalSite;
            await bot.SendMessage(chatId, "🌐 آدرس صفحه در سایت (localsite):\nمثال: AliRestaurant", cancellationToken: ct);
            break;

        case Step.LocalSite:
            session.Data.LocalSite = text;
            session.Step = Step.Title;
            await bot.SendMessage(chatId, "✏️ تیتر کسب‌وکار:", cancellationToken: ct);
            break;

        case Step.Title:
            session.Data.Title = text;
            session.Step = Step.Keywords;
            await bot.SendMessage(chatId, "🔑 کلمات کلیدی (با کاما):\nمثال: غذا,رستوران,کباب", cancellationToken: ct);
            break;

        case Step.Keywords:
            session.Data.Keywords = text;
            session.Step = Step.Description;
            await bot.SendMessage(chatId, "📝 توضیح مختصر:", cancellationToken: ct);
            break;

        case Step.Description:
            session.Data.Description = text;
            session.Step = Step.Phone;
            await bot.SendMessage(chatId, "📞 شماره تلفن:", cancellationToken: ct);
            break;

        case Step.Phone:
            session.Data.Phone = text;
            session.Step = Step.WorkingTime;
            await bot.SendMessage(chatId, "⏰ ساعت کاری:\nمثال: 09:00-22:00", cancellationToken: ct);
            break;

        case Step.WorkingTime:
            session.Data.WorkingTime = text;
            session.Step = Step.Discount;
            await bot.SendMessage(chatId, "💰 درصد تخفیف (اگر ندارید 0 بزنید):", cancellationToken: ct);
            break;

        case Step.Discount:
            if (!int.TryParse(text, out int disc)) { await bot.SendMessage(chatId, "❌ عدد وارد کنید.", cancellationToken: ct); return; }
            session.Data.Discount = disc;
            if (disc > 0)
            {
                session.Step = Step.DiscountDetail;
                await bot.SendMessage(chatId, "📋 توضیح تخفیف:", cancellationToken: ct);
            }
            else
            {
                session.Step = Step.Logo;
                await bot.SendMessage(chatId, "🖼 لوگو ارسال کنید (یا /skip):", cancellationToken: ct);
            }
            break;

        case Step.DiscountDetail:
            session.Data.DiscountDetail = text;
            session.Step = Step.Logo;
            await bot.SendMessage(chatId, "🖼 لوگو ارسال کنید (یا /skip):", cancellationToken: ct);
            break;

        case Step.Logo when text == "/skip":
            session.Step = Step.BusinessImage;
            await bot.SendMessage(chatId, "📸 تصویر اصلی ارسال کنید (یا /skip):", cancellationToken: ct);
            break;

        case Step.BusinessImage when text == "/skip":
            session.Step = Step.Confirm;
            await SendConfirmation(bot, chatId, session, ct);
            break;

        case Step.Confirm:
            if (text == "/confirm") await SubmitBusiness(bot, chatId, session, ct);
            else if (text == "/cancel") { sessions.Remove(chatId); await bot.SendMessage(chatId, "❌ لغو شد.", cancellationToken: ct); }
            break;
    }
}

async Task HandleCallback(ITelegramBotClient bot, CallbackQuery cb, CancellationToken ct)
{
    var chatId = cb.Message!.Chat.Id;
    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
    if (cb.Data == "start_register")
    {
        sessions[chatId] = new RegistrationSession();
        await bot.SendMessage(chatId, "🏪 نام کسب‌وکار را وارد کنید:", cancellationToken: ct);
    }
}

async Task HandlePhotoStep(ITelegramBotClient bot, Message msg, RegistrationSession session, long chatId, CancellationToken ct)
{
    string base64 = await GetBase64FromMessage(bot, msg, ct);
    switch (session.Step)
    {
        case Step.Logo:
            session.Data.Logo = base64;
            session.Step = Step.BusinessImage;
            await bot.SendMessage(chatId, "📸 تصویر اصلی ارسال کنید (یا /skip):", cancellationToken: ct);
            break;
        case Step.BusinessImage:
            session.Data.BusinessImage = base64;
            session.Step = Step.Confirm;
            await SendConfirmation(bot, chatId, session, ct);
            break;
    }
}

async Task<string> GetBase64FromMessage(ITelegramBotClient bot, Message msg, CancellationToken ct)
{
    string fileId = msg.Photo != null ? msg.Photo.Last().FileId : msg.Document!.FileId;
    var file = await bot.GetFile(fileId, ct);
    using var ms = new MemoryStream();
    await bot.DownloadFile(file.FilePath!, ms, ct);
    return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
}

async Task SendConfirmation(ITelegramBotClient bot, long chatId, RegistrationSession session, CancellationToken ct)
{
    var d = session.Data;
    var summary = $"""
        ✅ *خلاصه اطلاعات*

        🏪 نام: {d.StoreName}
        👤 مالک: {d.Owner}
        🆔 کد مشتری: {d.CustomerId}
        📂 دسته: {d.CatId}
        🏙 شهر: {d.City} | منطقه: {d.Region}
        📍 آدرس: {d.Address}
        🌐 localsite: {d.LocalSite}
        ✏️ تیتر: {d.Title}
        📞 تلفن: {d.Phone}
        ⏰ ساعت کاری: {d.WorkingTime}
        💰 تخفیف: {d.Discount}%
        🖼 لوگو: {(string.IsNullOrEmpty(d.Logo) ? "ندارد" : "✓")}
        📸 تصویر: {(string.IsNullOrEmpty(d.BusinessImage) ? "ندارد" : "✓")}

        برای تأیید /confirm و برای لغو /cancel بزنید.
        """;
    await bot.SendMessage(chatId, summary, parseMode: ParseMode.Markdown, cancellationToken: ct);
}

async Task SubmitBusiness(ITelegramBotClient bot, long chatId, RegistrationSession session, CancellationToken ct)
{
    var d = session.Data;
    var payload = new
    {
        customerid = d.CustomerId,
        userid = d.CustomerId, // یا یه userid ثابت اپراتوری
        type = 3,
        catid = d.CatId,
        storename = d.StoreName,
        owner = d.Owner,
        city = d.City,
        region = d.Region,
        address = d.Address,
        localsite = d.LocalSite,
        title = d.Title,
        keywords = d.Keywords,
        description = d.Description,
        discount = d.Discount,
        discountdetail = d.DiscountDetail ?? "",
        workingtime = d.WorkingTime,
        lat = 0.0,
        lon = 0.0,
        isShop = false,
        image = new
        {
            business = string.IsNullOrEmpty(d.BusinessImage) ? Array.Empty<string>() : new[] { d.BusinessImage },
            gallery = Array.Empty<string>(),
            cover = Array.Empty<string>(),
            logo = d.Logo ?? "",
            header = ""
        },
        Atrributes = new[]
        {
            new { id = 1, businessid = 0, name = "phone", value = d.Phone ?? "", que = 1, modifydate = DateTime.UtcNow.ToString("o") }
        }
    };

    var json = JsonConvert.SerializeObject(payload);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    try
    {
        await bot.SendMessage(chatId, "⏳ در حال ارسال...", cancellationToken: ct);
        var response = await httpClient.PostAsync(API_URL, content, ct);
        var result = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            await bot.SendMessage(chatId, "✅ کسب‌وکار با موفقیت ثبت شد!", cancellationToken: ct);
        else
            await bot.SendMessage(chatId, $"❌ خطا در ثبت:\n{result}", cancellationToken: ct);
    }
    catch (Exception ex)
    {
        await bot.SendMessage(chatId, $"❌ خطای اتصال: {ex.Message}", cancellationToken: ct);
    }
    sessions.Remove(chatId);
}

// ===================== MODELS =====================

enum Step { StoreName, Owner, CustomerId, CatId, City, Region, Address, LocalSite, Title, Keywords, Description, Phone, WorkingTime, Discount, DiscountDetail, Logo, BusinessImage, Confirm }

class BusinessData
{
    public int CustomerId { get; set; }
    public int CatId { get; set; }
    public string StoreName { get; set; } = "";
    public string Owner { get; set; } = "";
    public int City { get; set; }
    public string Region { get; set; } = "";
    public string Address { get; set; } = "";
    public string LocalSite { get; set; } = "";
    public string Title { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Description { get; set; } = "";
    public string Phone { get; set; } = "";
    public string WorkingTime { get; set; } = "";
    public int Discount { get; set; }
    public string? DiscountDetail { get; set; }
    public string? Logo { get; set; }
    public string? BusinessImage { get; set; }
}

class RegistrationSession
{
    public Step Step { get; set; } = Step.StoreName;
    public BusinessData Data { get; set; } = new();
    public bool IsOperator { get; set; }
}
