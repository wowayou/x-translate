using System.Globalization;
using XTranslate.App.Learning;
using XTranslate.Core.Learning;

namespace XTranslate.App;

internal static class LearningUi
{
    private static readonly IReadOnlyDictionary<string, (string Zh, string En)> Labels = new Dictionary<string, (string, string)>
    {
        ["Book"] = ("学习本", "Learning notebook"),
        ["Collect"] = ("收藏到学习本", "Save to learning notebook"),
        ["Editor"] = ("学习条目", "Learning item"),
        ["Privacy"] = ("仅保存你确认的内容到本地明文学习本，不发送网络请求。请核对预填原文／译文：它们不一定正确，也不一定是所选词语的释义。", "Only confirmed content is saved locally as plain text; no network request. Check prefilled source/translation: they may be incorrect and are not necessarily definitions of selected words."),
        ["BookHelp"] = ("个人学习材料，不是强制翻译术语。数据和备份为明文；不自动保存翻译历史，不云同步。", "Personal learning material, not forced translation terms. Data and backups are plain text; no automatic translation history or cloud sync."),
        ["Kind"] = ("类型", "Kind"), ["Origin"] = ("答案来源（自述，非权威认证）", "Answer origin (self-described, not certification)"),
        ["Word"] = ("单词", "Word"), ["Phrase"] = ("短语", "Phrase"), ["Pattern"] = ("句型／句子", "Pattern / sentence"),
        ["Personal"] = ("个人整理", "Personal"), ["MachineTranslation"] = ("机器译文（未验证）", "Machine translation (unverified)"),
        ["AiReview"] = ("AI 检查译文（可能误判）", "AI-reviewed translation (may be mistaken)"),
        ["SourceLanguage"] = ("学习材料语言", "Learning material language"), ["TargetLanguage"] = ("答案语言", "Answer language"),
        ["UnknownLanguage"] = ("未指定／未知", "Unspecified / unknown"),
        ["Prompt"] = ("词语／句型／回忆提示（必填，最多 1000 字符）", "Expression / recall prompt (required, max 1000 characters)"),
        ["Answer"] = ("释义／参考答案（必填，最多 4000 字符）", "Meaning / reference answer (required, max 4000 characters)"),
        ["IncludeContext"] = ("保存以下语境（可删减；最多 3000 字符）", "Save this context (editable, max 3000 characters)"),
        ["Context"] = ("语境", "Context"), ["Note"] = ("个人备注（最多 2000 字符）", "Personal note (max 2000 characters)"),
        ["EditHelp"] = ("改变学习材料／答案／语境／语言会重置该条目的复习进度；只改备注或来源不会重置。", "Changing material, answer, context or languages resets this item's recall schedule. Notes/origin alone do not."),
        ["Save"] = ("确认保存", "Confirm save"), ["Cancel"] = ("取消", "Cancel"), ["Close"] = ("关闭", "Close"),
        ["New"] = ("新建", "New"), ["Edit"] = ("编辑", "Edit"), ["Delete"] = ("删除", "Delete"),
        ["Export"] = ("备份导出", "Export backup"), ["Restore"] = ("恢复备份", "Restore backup"), ["Refresh"] = ("刷新", "Refresh"),
        ["Library"] = ("条目", "Items"), ["Review"] = ("复习", "Review"), ["DailyLimit"] = ("每日上限（UTC）", "Daily limit (UTC)"),
        ["List"] = ("学习条目列表", "Learning items"), ["Detail"] = ("条目详情", "Item details"),
        ["Summary"] = ("共 {0} 项 · 到期 {1} 项 · 今日已复习 {2}/{3} 项（按 UTC 日期）", "{0} items · {1} due · reviewed today {2}/{3} (UTC date)"),
        ["Due"] = ("下次复习：{0} · 已自评 {1} 次", "Next review: {0} · {1} self-ratings"),
        ["ReviewHelp"] = ("先回想，再揭示参考答案。自评只用于安排复习，不是语言能力评分。忘了：1 天；费力想起：保持间隔（至少 1 天）；轻松想起：首次 3 天，之后 2 倍＋1 天，最多 365 天。", "Recall first, then reveal the reference answer. Self-ratings only schedule reviews, not proficiency scores. Forgotten: 1 day; effortful: same interval (min 1 day); easy: initially 3 days, then 2× + 1, capped at 365 days."),
        ["RecallPrompt"] = ("回忆提示", "Recall prompt"), ["ReferenceAnswer"] = ("参考答案", "Reference answer"),
        ["HiddenAnswer"] = ("先尝试回想，再点击「揭示答案」。", "Try to recall, then click Reveal answer."),
        ["Reveal"] = ("揭示答案", "Reveal answer"), ["Skip"] = ("本轮跳过", "Skip this session"),
        ["Forgotten"] = ("忘了", "Forgotten"), ["Effortful"] = ("费力想起", "Effortful"), ["Easy"] = ("轻松想起", "Easy"),
        ["NothingDue"] = ("本轮没有待复习条目。可在条目页查看，或刷新重新排队。", "Nothing left to review this session. Browse items or refresh to rebuild the queue."),
        ["LimitReached"] = ("今日已达到你设置的上限。可以停止，也可以主动调整上限；不会强制追加任务。", "Your daily limit is reached. Stop here or explicitly adjust it; no forced extra tasks."),
        ["ConfirmDelete"] = ("确认删除选中的个人学习条目？删除后只能通过已有备份恢复。", "Delete the selected personal learning item? Recovery requires an existing backup."),
        ["ConfirmRestore"] = ("备份含 {0} 项、每日上限 {1}。确认用该备份完整替换当前 {2} 项及复习状态？不会合并；建议先导出现有数据备份。", "Backup: {0} items, daily limit {1}. Replace all {2} current items and recall state? This is not a merge; export a backup first."),
        ["BackupFilter"] = ("学习本 JSON 备份 (*.json)|*.json", "Learning notebook JSON backup (*.json)|*.json"),
        ["Working"] = ("正在处理本地数据…", "Working with local data…"), ["Saved"] = ("已保存到本地。", "Saved locally."),
        ["Restored"] = ("已恢复备份；旧预览不能覆盖本次恢复结果。", "Backup restored; older previews cannot overwrite this generation."),
        ["Exported"] = ("明文备份已导出，请妥善保存。", "Plain-text backup exported; keep it private."),
        ["Cancelled"] = ("已取消；尚未发布的改动未保存。", "Cancelled; unpublished changes were not saved."),
        ["Cancelling"] = ("正在取消；若已完成原子提交则仍视为已保存。", "Cancelling; an already published atomic commit still counts as saved."),
        ["CleanupPending"] = ("临时文件未能清理，可能含个人内容；请关闭占用后检查 data/learning 或备份目标目录。", "Temporary cleanup failed; files may contain personal content. Release file locks and inspect data/learning or the backup directory."),
        ["ErrorInvalidData"] = ("学习内容或文件无效：请检查必填项、长度、语言和格式。损坏的文件不会被自动清空。", "Invalid learning content/file: check required fields, lengths, languages and format. A damaged file is never reset silently."),
        ["ErrorTooLarge"] = ("学习数据超出条目数、文本或文件大小上限；未保存。", "Learning data exceeds count, text or file limits; not saved."),
        ["ErrorNotFound"] = ("条目已不存在，请刷新核对。", "Item no longer exists; refresh and check."),
        ["ErrorStaleRevision"] = ("条目已变化；草稿仍在，请先复制草稿，再取消并刷新核对，不会自动覆盖。", "Item changed; draft is retained. Copy it, cancel, then refresh and check; no automatic overwrite."),
        ["ErrorNotDue"] = ("该条目尚未到期，请刷新复习队列。", "This item is not due yet; refresh the queue."),
        ["ErrorDailyLimit"] = ("已达到今日上限，未追加复习。", "Daily limit reached; no extra review recorded."),
        ["ErrorInvalidTime"] = ("系统时间或复习时间无效／倒退；请核对时钟，未保存。", "Clock/review time is invalid or moved backwards; check the clock. Not saved."),
        ["ErrorIoFailure"] = ("本地读写失败；旧文件保持不变。可编辑草稿仍保留，请解除文件占用后重试。", "Local I/O failed; the previous file is unchanged and the editable draft is retained. Release locks and retry."),
        ["ErrorAccessDenied"] = ("目录或文件只读／无权限；旧数据和草稿保留，请换到可写目录后重试。", "Read-only/access-denied file or directory. Previous data/draft retained; use a writable directory and retry."),
        ["ErrorBusy"] = ("另一个操作占用学习本，请稍后手动重试。", "Another operation holds the notebook; retry manually later."),
        ["ErrorUnsafePath"] = ("拒绝链接路径或覆盖应用管理的数据目录。", "Link paths or overwriting managed application data are not allowed."),
        ["ErrorConflict"] = ("学习本已被其它操作更新；未覆盖。草稿仍在，请先复制草稿，再取消并刷新核对。", "Notebook changed in another operation; not overwritten. Draft retained: copy it, cancel, then refresh and check."),
        ["ErrorAlreadyExists"] = ("目标文件已存在，需要明确确认覆盖。", "Destination exists; explicit overwrite confirmation is required."),
        ["ErrorUnknown"] = ("学习本操作失败，未宣称保存成功；请保留草稿并核对文件。", "Notebook operation failed; save was not claimed successful. Keep your draft and check the file.")
    };
    internal static string Text(string key)
    {
        var value = Labels[key]; return UiLocalization.EffectiveLanguage == UiLanguage.English ? value.En : value.Zh;
    }
    internal static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);
    internal static string Error(Exception error)
    {
        var key = error switch
        {
            LearningException e => "Error" + e.Code,
            LearningStorageException e => "Error" + e.Code,
            OperationCanceledException => "Cancelled",
            _ => "ErrorUnknown"
        };
        return Text(key) + (error is LearningStorageException { CleanupPending: true } ? " " + Text("CleanupPending") : "");
    }
}
