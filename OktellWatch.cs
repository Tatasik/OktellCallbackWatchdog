// ============================================================================
//  Oktell Watch — сторож формы обратного звонка Oktell
// ----------------------------------------------------------------------------
//  Назначение:
//    Штатная форма задачи обратного звонка в Oktell появляется тихо и часто
//    остаётся на заднем плане. Программа мгновенно ловит показ нового окна
//    по событиям Windows (SetWinEventHook), страхуясь полным опросом всех
//    окон (EnumWindows / EnumChildWindows) раз в 15 секунд. Найдя окно,
//    совпадающее с правилами, подаёт звук, выводит окно Oktell наверх и
//    мигает его кнопкой на панели задач — пока форма не обработана.
//
//  Сборка (на любом Windows 10/11, ничего устанавливать не нужно):
//    build.bat   — компилирует OktellWatch.exe штатным компилятором
//                  .NET Framework (csc.exe из C:\Windows\Microsoft.NET)
//    Либо вручную:
//      %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe ^
//        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
//        /r:System.Windows.Forms.dll /out:OktellWatch.exe OktellWatch.cs
//
//  Использование:
//    1. Конфиг config.ini лежит в подпапке system рядом с программой
//       (создаётся при первом пуске; при обновлении со старых версий
//       переносится туда автоматически).
//    2. Запустите OktellWatch.exe — значок появится возле часов.
//    3. Правый клик по значку: Диспетчер правил, Инспектор окон, Пауза,
//       Справка, Выход.
//    4. Диспетчер правил — добавить/изменить/отключить/удалить правило
//       галочками, без правки config.ini (для операторов).
//    5. Инспектор → «Окно под курсором» → навести на форму Oktell →
//       Ctrl+Пробел → «Добавить в наблюдение» (правило пишется в config.ini).
//
//  Кодировка этого файла: UTF-8 (обязательно с BOM — иначе русский текст в
//  строках будет искажён при сборке csc.exe).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Timer здесь — всегда таймер WinForms (для UI-потока)
using Timer = System.Windows.Forms.Timer;

[assembly: System.Reflection.AssemblyTitle("Oktell Watch")]
[assembly: System.Reflection.AssemblyProduct("Oktell Watch")]
[assembly: System.Reflection.AssemblyDescription("Сторож формы обратного звонка Oktell: мгновенно ловит беззвучную форму задачи, подаёт звуковой сигнал и выводит окно наверх. Открытый исходный код: github.com/Tatasik/OktellCallbackWatchdog")]
[assembly: System.Reflection.AssemblyCompany("Tatasik — github.com/Tatasik")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 Tatasik. Лицензия MIT.")]
[assembly: System.Reflection.AssemblyTrademark("")]
[assembly: System.Reflection.AssemblyVersion("1.2.3.7")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.3.7")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.2.3.7")]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace OktellWatch
{
    // ------------------------------------------------------------------ POINT
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    // --------------------------------------------------------------- FLASHWINFO
    [StructLayout(LayoutKind.Sequential)]
    internal struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    // ------------------------------------------------------------------ WinApi
    internal static class WinApi
    {
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        public const uint GA_ROOT = 2;
        public const int SW_RESTORE = 9;
        public const uint FLASHW_ALL = 3;
        public const uint FLASHW_TIMERNOFG = 12;

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool FlashWindowEx(ref FLASHWINFO fw);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        // Подсказка-заполнитель внутри поля ввода (исчезает, когда начат ввод)
        public const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void SetCue(Control c, string text)
        {
            try { SendMessage(c.Handle, EM_SETCUEBANNER, (IntPtr)1, text); }
            catch { }
        }

        // Хук событий окон: мгновенное узнавание о показе нового окна
        // и о закрытии наблюдаемого (v1.1 показ, v1.2 + закрытие)
        public delegate void WinEventDelegate(IntPtr hHook, uint evt, IntPtr hwnd,
            int idObject, int idChild, uint thread, uint time);

        public const uint EVENT_OBJECT_DESTROY = 0x8001;   // окно уничтожено
        public const uint EVENT_OBJECT_SHOW = 0x8002;      // окно показано
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;  // колбэк в message loop нашего потока

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, WinEventDelegate proc, uint idProcess, uint idThread, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        // Экстренное глушение звука: PlaySound(NULL, NULL, SND_PURGE) останавливает
        // всё, что проигрывается на текущем потоке через winmm — в том числе
        // системные сигналы SystemSounds, которые иначе не остановить.
        public const int SND_ASYNC = 0x0001;
        public const int SND_PURGE = 0x0040;

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern bool PlaySound(string pszSound, IntPtr hmod, int fdwSound);

        public static void EnableDpiAware()
        {
            try { SetProcessDPIAware(); } catch { }
        }

        public static void Flash(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero || !IsWindow(h)) return;
                FLASHWINFO fw = new FLASHWINFO();
                fw.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                fw.hwnd = h;
                fw.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG;
                fw.uCount = 0;
                fw.dwTimeout = 0;
                FlashWindowEx(ref fw);
            }
            catch { }
        }

        public static void ForceForeground(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero || !IsWindow(h)) return;
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                // трюк с нажатием/отпусканием ALT — Windows разрешает стороннему
                // процессу перехватить фокус только после недавнего ввода
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SetForegroundWindow(h);
                if (GetForegroundWindow() != h) SwitchToThisWindow(h, true);
            }
            catch { }
        }
    }

    // -------------------------------------------------------------------- Str
    internal static class Str
    {
        public static bool IsEmpty(string s) { return s == null || s.Trim().Length == 0; }

        public static string Lo(string s) { return s == null ? "" : s.Trim().ToLowerInvariant(); }

        public static bool ContainsIC(string where, string what)
        {
            if (IsEmpty(what)) return true;
            return Lo(where).Contains(Lo(what));
        }

        public static string TrimTo(string s, int n)
        {
            if (String.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            return s.Length <= n ? s : s.Substring(0, Math.Max(1, n - 3)) + "...";
        }

        // Строка без многоточия — для ПОСТРОЕНИЯ правил. TrimTo дописывает
        // «...» для показа в интерфейсе; правило, собранное по такому тексту,
        // искало бы три точки, которых в реальном окне нет, и никогда бы не
        // сработало (заголовки форм Oktell длиннее 40 символов — штатный
        // сценарий настройки через инспектор выдавал мёртвое правило).
        public static string Cut(string s, int n)
        {
            if (String.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }

    // -------------------------------------------------------------- WindowInfo
    internal class WindowInfo
    {
        public IntPtr Hwnd;
        public IntPtr RootHwnd;      // корневое окно верхнего уровня (для child)
        public bool IsChild;
        public string ClassName = "";
        public string Title = "";
        public string ProcessName = "";
        public int Pid;

        public string Describe()
        {
            return "hwnd=0x" + Hwnd.ToInt64().ToString("X") +
                   " proc=" + (String.IsNullOrEmpty(ProcessName) ? "?" : ProcessName) +
                   " class=" + ClassName +
                   " title=\"" + Title + "\"";
        }
    }

    // --------------------------------------------------------------- WatchRule
    // Правило наблюдения: proc:...;title:...;class:...;scope:top|child|any
    // Совпадение = ВСЕ указанные поля найдены в окне (без учёта регистра).
    // Исключения (с 1.2): proc!:...;title!:...;class!:... — поле НЕ должно
    // найтись в окне. Работают вместе с обычными признаками: правило
    // срабатывает, только если ни одно исключение не найдено. С 1.2.3.3
    // title!: и class!: проверяются по ВСЕМУ дереву корневого окна —
    // заголовок корня и все его видимые внутренние элементы (раньше
    // исключение смотрело только в совпавший элемент, поэтому фраза из
    // другого элемента той же формы — например, строка «Идёт дозвон…» —
    // исключение не включала).
    // Параметр btn: из конфигов до 1.2.3.2 игнорируется (проверка кнопок
    // снята с поддержки: наблюдение работает по текстовым приметам окна).
    internal class WatchRule
    {
        public string Proc;
        public string Title;
        public string Class;
        public string NotProc;    // proc!:  — процесс НЕ должен содержать
        public string NotTitle;   // title!: — фраза НЕ должна найтись нигде в окне
        public string NotClass;   // class!: — класс НЕ должен найтись нигде в окне
        public string Scope = "top";
        public string Raw = "";
        public bool Enabled = true;   // false = правило отключено (off=true)
        // С 1.2.3.7: фрагменты правила с неизвестными ключами («titl:» из
        // опечатки и т.п.) больше не выбрасываются молча — загрузщик пишет
        // их в журнал; правило при этом разбирается как раньше
        public List<string> Warnings = new List<string>();

        public static WatchRule Parse(string raw)
        {
            if (Str.IsEmpty(raw)) return null;
            WatchRule r = new WatchRule();
            r.Raw = raw.Trim();
            string[] parts = r.Raw.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                // выключенное правило (диспетчер правил); допускаем и off=true, и off:true
                if (p.StartsWith("off=", StringComparison.OrdinalIgnoreCase))
                {
                    string ov = p.Substring(4).Trim().ToLowerInvariant();
                    r.Enabled = !(ov == "1" || ov == "true" || ov == "on" || ov == "yes");
                    continue;
                }
                int c = p.IndexOf(':');
                if (c <= 0)
                {
                    // с 1.2.3.7 фрагмент без ключа не исчезает молча
                    r.Warnings.Add("фрагмент \"" + p + "\" без ключа — проигнорирован");
                    continue;
                }
                string k = p.Substring(0, c).Trim().ToLowerInvariant();
                string v = p.Substring(c + 1).Trim();
                if (k == "proc") r.Proc = v;
                else if (k == "title") r.Title = v;
                else if (k == "class") r.Class = v;
                else if (k == "proc!") r.NotProc = v;
                else if (k == "title!") r.NotTitle = v;
                else if (k == "class!") r.NotClass = v;
                else if (k == "scope") r.Scope = v.ToLowerInvariant();
                else if (k == "off")
                {
                    string lv = v.ToLowerInvariant();
                    r.Enabled = !(lv == "1" || lv == "true" || lv == "on" || lv == "yes");
                }
                else if (k == "btn" || k == "btn!")
                {
                    // параметр btn: снят с поддержки (1.2.3.2) — игнорируем
                    // молча, чтобы старые конфиги не сыпали предупреждениями
                }
                else
                {
                    // с 1.2.3.7: неизвестный ключ («titl:» из опечатки) больше
                    // не превращает правило молча в «все окна процесса» —
                    // загрузщик запишет фрагмент в журнал
                    r.Warnings.Add("неизвестный ключ \"" + k + ":\" (фрагмент \"" +
                        p + "\") — сегмент проигнорирован, проверьте написание");
                }
            }
            // хотя бы один ОБЫЧНЫЙ признак обязателен (правило только из
            // исключений не принимаем — оно ловило бы вообще все окна)
            if (Str.IsEmpty(r.Proc) && Str.IsEmpty(r.Title) && Str.IsEmpty(r.Class)) return null;
            if (r.Scope != "top" && r.Scope != "child" && r.Scope != "any") r.Scope = "top";
            return r;
        }

        public bool Matches(WindowInfo w)
        {
            if (!Enabled) return false;
            string sc = Str.Lo(Scope);
            if (sc == "child" && !w.IsChild) return false;
            if (sc == "top" && w.IsChild) return false;
            if (!Str.IsEmpty(Proc) && !Str.ContainsIC(w.ProcessName, Proc)) return false;
            if (!Str.IsEmpty(Title) && !Str.ContainsIC(w.Title, Title)) return false;
            if (!Str.IsEmpty(Class) && !Str.ContainsIC(w.ClassName, Class)) return false;
            // исключения: исключающая подстрока найдена — окно не подходит
            if (!Str.IsEmpty(NotProc) && Str.ContainsIC(w.ProcessName, NotProc)) return false;
            if (!Str.IsEmpty(NotTitle) && Str.ContainsIC(w.Title, NotTitle)) return false;
            if (!Str.IsEmpty(NotClass) && Str.ContainsIC(w.ClassName, NotClass)) return false;
            return !Str.IsEmpty(Proc) || !Str.IsEmpty(Title) || !Str.IsEmpty(Class);
        }

        public string Describe()
        {
            return "proc=" + (Proc == null ? "*" : Proc) +
                   " title=" + (Title == null ? "*" : Title) +
                   " class=" + (Class == null ? "*" : Class) +
                   (Str.IsEmpty(NotProc) ? "" : " proc!~" + NotProc) +
                   (Str.IsEmpty(NotTitle) ? "" : " title!~" + NotTitle) +
                   (Str.IsEmpty(NotClass) ? "" : " class!~" + NotClass) +
                   " scope=" + (Scope == null ? "top" : Scope);
        }

        // Собрать текст правила заново из полей (канонический вид для config.ini)
        public string ToRaw()
        {
            StringBuilder sb = new StringBuilder();
            if (!Enabled) sb.Append("off:true;");
            if (!Str.IsEmpty(Proc)) sb.Append("proc:").Append(Proc.Trim()).Append(';');
            if (!Str.IsEmpty(Title)) sb.Append("title:").Append(Title.Trim()).Append(';');
            if (!Str.IsEmpty(Class)) sb.Append("class:").Append(Class.Trim()).Append(';');
            if (!Str.IsEmpty(NotProc)) sb.Append("proc!:").Append(NotProc.Trim()).Append(';');
            if (!Str.IsEmpty(NotTitle)) sb.Append("title!:").Append(NotTitle.Trim()).Append(';');
            if (!Str.IsEmpty(NotClass)) sb.Append("class!:").Append(NotClass.Trim()).Append(';');
            sb.Append("scope:").Append(Str.IsEmpty(Scope) ? "top" : Scope.Trim().ToLowerInvariant());
            return sb.ToString();
        }

        // Правило-черновик по конкретному окну (для «Добавить в наблюдение»)
        public static WatchRule FromWindow(WindowInfo w)
        {
            WatchRule r = new WatchRule();
            if (!Str.IsEmpty(w.ProcessName)) r.Proc = w.ProcessName.Trim();
            string t = (w.Title == null) ? "" : w.Title.Trim();
            if (t.Length > 0) r.Title = Str.Cut(t, 40);
            if (Str.IsEmpty(r.Title) && !Str.IsEmpty(w.ClassName)) r.Class = w.ClassName.Trim();
            r.Scope = w.IsChild ? "child" : "top";
            return r;
        }

        // Человекочитаемое описание — для диспетчера правил
        public string DescribeFriendly()
        {
            List<string> p = new List<string>();
            if (!Str.IsEmpty(Proc)) p.Add("процесс ~«" + Proc.Trim() + "»");
            if (!Str.IsEmpty(Title)) p.Add("заголовок ~«" + Title.Trim() + "»");
            if (!Str.IsEmpty(Class)) p.Add("класс ~«" + Class.Trim() + "»");
            if (!Str.IsEmpty(NotProc)) p.Add("процесс НЕ ~«" + NotProc.Trim() + "»");
            if (!Str.IsEmpty(NotTitle)) p.Add("в окне НЕТ ~«" + NotTitle.Trim() + "»");
            if (!Str.IsEmpty(NotClass)) p.Add("класса НЕТ в окне ~«" + NotClass.Trim() + "»");
            string sc = Str.Lo(Scope);
            if (sc == "child") p.Add("только вложенные окна");
            else if (sc == "any") p.Add("главные и вложенные окна");
            else p.Add("только главные окна");
            return String.Join("; ", p.ToArray());
        }
    }

    // ------------------------------------------------------------------ RootScan
    // Скан текстов и классов всего дерева корневого окна (с 1.2.3.3):
    // заголовок/класс корня + тексты/классы всех видимых дочерних окон.
    // Нужен для исключений title!: / class!: — фраза не должна найтись
    // НИГДЕ в окне, а не только в совпавшем элементе.
    internal sealed class RootScan
    {
        public readonly List<string> Texts = new List<string>();
        public readonly List<string> Classes = new List<string>();

        public bool HasText(string s)
        {
            for (int i = 0; i < Texts.Count; i++)
                if (Str.ContainsIC(Texts[i], s)) return true;
            return false;
        }

        public bool HasClass(string s)
        {
            for (int i = 0; i < Classes.Count; i++)
                if (Str.ContainsIC(Classes[i], s)) return true;
            return false;
        }

        // true = по дереву окна найдено исключение правила
        public bool Rejects(WatchRule r)
        {
            if (!Str.IsEmpty(r.NotTitle) && HasText(r.NotTitle)) return true;
            if (!Str.IsEmpty(r.NotClass) && HasClass(r.NotClass)) return true;
            return false;
        }
    }

    // ------------------------------------------------------------------ IniFile
    // Простой INI-парсер с сохранением комментариев и порядка строк.
    // Файл должен быть в UTF-8 (с BOM или без).
    internal class IniFile
    {
        private readonly string _path;
        private List<string> _lines = new List<string>();

        public IniFile(string path)
        {
            _path = path;
            Reload();
        }

        private static string ReadText(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            try
            {
                Encoding strict = (Encoding)Encoding.UTF8.Clone();
                strict.DecoderFallback = new DecoderExceptionFallback();
                return strict.GetString(b, 0, b.Length);
            }
            catch (DecoderFallbackException)
            {
                try { return Encoding.GetEncoding(1251).GetString(b, 0, b.Length); }
                catch { return Encoding.Default.GetString(b, 0, b.Length); }
            }
        }

        public void Reload()
        {
            _lines = new List<string>();
            if (File.Exists(_path))
            {
                string text = ReadText(_path);
                string[] arr = text.Replace("\r\n", "\n").Split('\n');
                _lines.AddRange(arr);
            }
        }

        public void Save()
        {
            File.WriteAllText(_path, String.Join(Environment.NewLine, _lines.ToArray()) + Environment.NewLine, Encoding.UTF8);
        }

        public string Get(string section, string key, string def)
        {
            string cur = null;
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i].Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    cur = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || cur == null) continue;
                if (cur != section.ToLowerInvariant()) continue;
                if (String.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return ScalarValue(line.Substring(eq + 1));
            }
            return def;
        }

        // Значение обычной настройки без хвостового комментария (с 1.2.3.6):
        // «pollIntervalMs=2000 ; страховка» раньше возвращалось целиком, и
        // настройка молча подменялась значением по умолчанию (число не
        // разбиралось, путь к звуку переставал существовать). Секция
        // [watch] читается через SectionItems без обрезки: в строках rule*
        // «;» — разделитель полей правила, а не начало комментария.
        private static string ScalarValue(string v)
        {
            int sc = v.IndexOf(';');
            if (sc >= 0) v = v.Substring(0, sc);
            return v.Trim();
        }

        public int GetInt(string section, string key, int def)
        {
            int v;
            if (int.TryParse(Get(section, key, null), out v)) return v;
            return def;
        }

        public bool GetBool(string section, string key, bool def)
        {
            string s = Get(section, key, null);
            if (s == null) return def;
            s = s.ToLowerInvariant();
            if (s == "1" || s == "true" || s == "on" || s == "yes") return true;
            if (s == "0" || s == "false" || s == "off" || s == "no") return false;
            return def;
        }

        public List<KeyValuePair<string, string>> SectionItems(string section)
        {
            List<KeyValuePair<string, string>> res = new List<KeyValuePair<string, string>>();
            string cur = null;
            string sec = section.ToLowerInvariant();
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i].Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    cur = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                if (cur != sec) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                res.Add(new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
            }
            return res;
        }

        // Заменить значение или вставить новую строку в конец секции
        public void SetValue(string section, string key, string value)
        {
            int curSection = -1;
            int insertAt = _lines.Count;
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i].Trim();
                bool isHeader = line.StartsWith("[") && line.EndsWith("]");
                if (isHeader)
                {
                    if (curSection >= 0) { insertAt = i; break; }
                    string name = line.Substring(1, line.Length - 2).Trim();
                    if (String.Equals(name, section, StringComparison.OrdinalIgnoreCase)) curSection = i;
                }
                else if (curSection >= 0 && line.Length > 0 && !line.StartsWith(";") && !line.StartsWith("#"))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && String.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        _lines[i] = key + "=" + value;
                        Save();
                        return;
                    }
                }
            }
            if (curSection < 0)
            {
                while (_lines.Count > 0 && _lines[_lines.Count - 1].Trim().Length == 0) _lines.RemoveAt(_lines.Count - 1);
                if (_lines.Count > 0) _lines.Add("");
                _lines.Add("[" + section + "]");
                _lines.Add(key + "=" + value);
            }
            else
            {
                _lines.Insert(insertAt, key + "=" + value);
            }
            Save();
        }

        // Удалить ключ из секции (первое вхождение). Вернёт true, если удалён.
        public bool DeleteKey(string section, string key)
        {
            int curSection = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i].Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    curSection = String.Equals(line.Substring(1, line.Length - 2).Trim(),
                        section, StringComparison.OrdinalIgnoreCase) ? i : -1;
                    continue;
                }
                if (curSection < 0 || line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq > 0 && String.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    _lines.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }
    }

    // ----------------------------------------------------------------- Settings
    internal class Settings
    {
        public string ConfigPath;
        public IniFile Ini;
        public DateTime Stamp;
        public List<string> BadRules = new List<string>();
        // С 1.2.3.7: предупреждения о неизвестных ключах в разобранных
        // правилах (строки, не разобравшиеся целиком, — в BadRules)
        public List<string> RuleWarnings = new List<string>();

        public int PollIntervalMs = 1000;
        public bool UseHooks = true;       // мгновенное обнаружение по событиям Windows
        public int HookCooldownMs = 250;   // троттлинг обработки событий показа
        public int ListRefreshMs = 2000;   // период обновления списка инспектора
        public int ProcCacheSec = 15;      // время жизни кэша имён процессов
        public bool VisibleOnly = true;
        public List<WatchRule> Rules = new List<WatchRule>();

        public string SoundFile = @"C:\Windows\Media\Alarm05.wav";
        public bool RepeatAlert = true;
        public int RepeatEverySec = 5;
        public bool BringToFront = true;
        public bool FlashTaskbar = true;

        public bool LogEnabled = true;
        public string LogFolder = "system\\logs";

        public string LogFolderAbsolute
        {
            get
            {
                try
                {
                    if (Path.IsPathRooted(LogFolder)) return LogFolder;
                    return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFolder);
                }
                catch { return "system\\logs"; }
            }
        }

        public Settings(string path) { ConfigPath = path; }

        public bool EnsureExists()
        {
            if (File.Exists(ConfigPath)) return true;
            try
            {
                string text = DefaultConfigText.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                File.WriteAllText(ConfigPath, text, Encoding.UTF8);
                return true;
            }
            catch { return false; }
        }

        public void Load()
        {
            Ini = new IniFile(ConfigPath);
            ReadAll();
            try { Stamp = File.GetLastWriteTimeUtc(ConfigPath); } catch { }
        }

        public void ReadAll()
        {
            PollIntervalMs = Ini.GetInt("watch", "pollIntervalMs", 1000);
            UseHooks = Ini.GetBool("watch", "useHooks", true);
            HookCooldownMs = Ini.GetInt("watch", "hookCooldownMs", 250);
            ListRefreshMs = Ini.GetInt("watch", "listRefreshMs", 2000);
            ProcCacheSec = Ini.GetInt("watch", "procCacheSec", 15);
            VisibleOnly = Ini.GetBool("watch", "visibleOnly", true);
            SoundFile = Ini.Get("alert", "soundFile", @"C:\Windows\Media\Alarm05.wav");
            RepeatAlert = Ini.GetBool("alert", "repeatAlert", true);
            RepeatEverySec = Ini.GetInt("alert", "repeatEverySec", 5);
            BringToFront = Ini.GetBool("alert", "bringToFront", true);
            FlashTaskbar = Ini.GetBool("alert", "flashTaskbar", true);
            LogEnabled = Ini.GetBool("log", "enabled", true);
            LogFolder = Ini.Get("log", "folder", "system\\logs");

            Rules.Clear();
            BadRules.Clear();
            RuleWarnings.Clear();
            List<KeyValuePair<string, string>> items = Ini.SectionItems("watch");
            for (int i = 0; i < items.Count; i++)
            {
                string k = items[i].Key.ToLowerInvariant();
                if (!k.StartsWith("rule")) continue;
                WatchRule r = WatchRule.Parse(items[i].Value);
                if (r != null)
                {
                    Rules.Add(r);
                    for (int w = 0; w < r.Warnings.Count; w++)
                        RuleWarnings.Add(items[i].Key + ": " + r.Warnings[w]);
                }
                else BadRules.Add(items[i].Key + "=" + items[i].Value);
            }
        }

        public const string DefaultConfigText =
@"; ============================================================
;  Oktell Watch — настройки (кодировка файла: UTF-8)
;  Можно править в Блокноте прямо во время работы:
;  программа перечитает файл в течение секунды.
;  После значения обычной настройки можно писать пояснение
;  через « ; » (пример: pollIntervalMs=15000 ; раз в 15 секунд);
;  в строках rule* «;» — разделитель полей: пояснения —
;  только отдельной строкой выше.
;  Правила удобно вести через меню значка — «Диспетчер правил...».
; ============================================================

[watch]
; Как часто полностью просматривать все окна, мс (страховка).
; Новые окна ловятся мгновенно по событиям Windows (useHooks=true),
; поэтому полный опрос выполняется не чаще, чем раз в 15 секунд.
pollIntervalMs=15000

; Мгновенное обнаружение по событиям Windows (рекомендуется).
; false — работать по-старому: только опрос каждые pollIntervalMs.
useHooks=true

; Учитывать только видимые окна
visibleOnly=true

; --- Правила наблюдения ---
; Формат:  rule<N>=proc:...;title:...;class:...;scope:...
;   proc:   часть имени процесса (например oktell)
;   title:  часть заголовка окна
;   class:  часть класса окна
;   scope:  top   — окна верхнего уровня (по умолчанию)
;           child — внутренние окна (элементы внутри главного окна)
;           any   — искать и там, и там
;   proc!:  процесс НЕ должен содержать (исключение)
;   title!: фраза НЕ должна найтись нигде в окне — в заголовке и во
;           внутренних элементах (с 1.2.3.3)
;   class!: класс НЕ должен найтись нигде в окне (с 1.2.3.3)
; Регистр не важен. Совпадение = ВСЕ указанные поля найдены в окне.
; Исключения (поля со знаком «!») сужают правило: окно подходит, только
; если исключающая подстрока НЕ найдена нигде в окне. Хотя бы один
; обычный признак (без «!») в правиле обязателен.
; Несколько правил работают как «ИЛИ». Лишнее поле просто не указывайте.
; Выключенное правило помечается off:true (это делает Диспетчер правил).
; Добавлять и убирать правила удобнее через «Диспетчер правил...» в меню
; значка у часов — править файл вручную не обязательно.

; Основное правило: процесс Oktell, внутри формы есть элемент с текстом
; «зарезерв» (строка «Вы зарезервированы на выполнение задачи» — она есть
; во всех задачах обратного звонка; scope:any ищет её и среди внутренних
; элементов формы). Окно дозвона игнорируется: пока внизу формы видно
; «Идёт дозвон на номер…» (после подтверждения звонка), исключение
; title!:дозвон глушит сигнал; в момент резерва этой фразы в окне нет.
rule1=proc:oktell;title:зарезерв;title!:дозвон;scope:any

; Примеры (включите галочкой в Диспетчере правил или раскомментируйте):
;rule2=proc:oktell;title:перезвон;scope:top
;rule3=proc:oktell;title:заказ звонка;scope:top
;rule4=proc:oktell;title:забронир;scope:top
;rule5=proc:oktell;title:обратн;scope:child

; Исключение дозвона: ловим «обратн», но НЕ окна дозвона, которые
; появляются после подтверждения звонка (тот же приём, что в rule1;
; точную фразу подскажет инспектор «Окно под курсором» в справке):
;rule7=proc:oktell;title:обратн;title!:дозвон;scope:any

; --- Тонкая настройка (обычно менять не нужно) ---
; Реагировать на события показа окон не чаще, чем раз в N мс
;hookCooldownMs=250
; Период обновления списка окон в инспекторе, мс
;listRefreshMs=2000
; Время жизни кэша имён процессов, сек
;procCacheSec=15

[alert]
; Звуковой файл (wav). Если файла нет — системный сигнал.
soundFile=C:\Windows\Media\Alarm05.wav

; Повторять сигнал, пока форма не обработана
repeatAlert=true

; Период повтора сигнала, сек
repeatEverySec=5

; Звук обрывается мгновенно, как только закрыта последняя наблюдаемая
; форма (не доигрывает до конца файла — чтобы сигнал не попал в эфир,
; когда оператор уже говорит с клиентом). Закрытие всей формы гасит
; звук и тогда, когда отслеживался её внутренний элемент (с 1.2.3.5).
; Пауза наблюдения глушит звук тоже; отдельно это поведение не
; настраивается.

; Выводить окно Oktell наверх при первом обнаружении формы
bringToFront=true

; Мигать кнопкой на панели задач
flashTaskbar=true

[log]
; Вести журнал (подпапка system рядом с программой)
enabled=true
folder=system\logs

";
    }

    // --------------------------------------------------------------------- Log
    internal static class Log
    {
        private static readonly object Locker = new object();
        public static bool Enabled = true;
        public static string Folder = "logs";

        public static void Write(string ev, string details)
        {
            if (!Enabled) return;
            try
            {
                lock (Locker)
                {
                    Directory.CreateDirectory(Folder);
                    string file = Path.Combine(Folder, "OktellWatch_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + ev.PadRight(9) + "| " + details;
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- Collector
    // Сбор и сопоставление окон с правилами
    internal static class Collector
    {
        // Кэш имён процессов с временем жизни: имена процессов меняются редко,
        // поэтому у системы спрашиваем не чаще раза в ProcCacheSec секунд на PID
        private sealed class ProcNameCache
        {
            private class Ent { public string Name; public int Stamp; }
            private readonly Dictionary<int, Ent> _map = new Dictionary<int, Ent>();
            private int _ttlMs;

            public ProcNameCache(int ttlMs) { _ttlMs = ttlMs; }

            public void SetTtlMs(int ms) { if (ms > 1000) _ttlMs = ms; }

            public string Get(int pid)
            {
                Ent e;
                if (_map.TryGetValue(pid, out e))
                {
                    int age = unchecked(Environment.TickCount - e.Stamp);
                    if (age >= 0 && age < _ttlMs) return e.Name;
                }
                string n = "";
                try { using (Process p = Process.GetProcessById(pid)) { n = p.ProcessName; } }
                catch { }
                Ent ne = new Ent();
                ne.Name = n;
                ne.Stamp = Environment.TickCount;
                _map[pid] = ne;
                return n;
            }
        }

        private static readonly ProcNameCache _cache = new ProcNameCache(15000);
        private static readonly int MyPid;
        private static bool VisibleOnly = true;

        static Collector()
        {
            try { MyPid = Process.GetCurrentProcess().Id; } catch { MyPid = 0; }
        }

        public static int OwnPid { get { return MyPid; } }

        public static string ProcessName(int pid)
        {
            if (pid <= 0) return "";
            return _cache.Get(pid);
        }

        public static void SetCacheTtl(int seconds)
        {
            if (seconds < 1) seconds = 1;
            _cache.SetTtlMs(seconds * 1000);
        }

        // Прочитать окно. includeSelf=true — не отфильтровывать собственные окна
        public static WindowInfo ReadWindow(IntPtr h, bool isChild, bool includeSelf)
        {
            try
            {
                if (h == IntPtr.Zero || !WinApi.IsWindow(h)) return null;
                if (VisibleOnly && !WinApi.IsWindowVisible(h)) return null;

                uint pid;
                WinApi.GetWindowThreadProcessId(h, out pid);
                if (!includeSelf && (int)pid == MyPid) return null;

                WindowInfo wi = new WindowInfo();
                wi.Hwnd = h;
                wi.IsChild = isChild;
                wi.Pid = (int)pid;
                wi.ProcessName = ProcessName(wi.Pid);

                StringBuilder cls = new StringBuilder(512);
                WinApi.GetClassName(h, cls, 512);
                wi.ClassName = cls.ToString();

                int len = WinApi.GetWindowTextLength(h);
                if (len > 0)
                {
                    StringBuilder sb = new StringBuilder(len + 2);
                    WinApi.GetWindowText(h, sb, sb.Capacity);
                    wi.Title = sb.ToString();
                }

                if (!isChild) wi.RootHwnd = h;
                return wi;
            }
            catch { return null; }
        }

        public static WindowInfo ReadWindow(IntPtr h, bool isChild)
        {
            return ReadWindow(h, isChild, false);
        }

        // Журнал «IGNORED»: окна, пропущенные исключением (одна запись
        // на hwnd за всё время работы — чтобы не засорять журнал).
        private static readonly HashSet<long> _ignoredLogged = new HashSet<long>();

        // Скан дерева корня: заголовок/класс корня + тексты/классы дочерних
        private static RootScan ScanRoot(WindowInfo top, List<WindowInfo> children)
        {
            RootScan rs = new RootScan();
            if (!Str.IsEmpty(top.Title)) rs.Texts.Add(top.Title);
            if (!Str.IsEmpty(top.ClassName)) rs.Classes.Add(top.ClassName);
            if (children != null)
                for (int i = 0; i < children.Count; i++)
                {
                    if (!Str.IsEmpty(children[i].Title)) rs.Texts.Add(children[i].Title);
                    if (!Str.IsEmpty(children[i].ClassName)) rs.Classes.Add(children[i].ClassName);
                }
            return rs;
        }

        // Скан дерева для окна верхнего уровня (дочерние перечисляются на месте)
        private static RootScan ScanRootTop(WindowInfo top)
        {
            List<WindowInfo> children = new List<WindowInfo>();
            WinApi.EnumChildWindows(top.Hwnd, delegate(IntPtr ch, IntPtr lp)
            {
                WindowInfo ci = ReadWindow(ch, true);
                if (ci != null) children.Add(ci);
                return true;
            }, IntPtr.Zero);
            return ScanRoot(top, children);
        }

        // Записать в журнал, что окно пропущено исключением правила
        private static void NoteIgnored(WatchRule r, WindowInfo wi, int ruleNo)
        {
            if (_ignoredLogged.Add(wi.Hwnd.ToInt64()))
                Log.Write("IGNORED", "[правило " + (ruleNo + 1) + "] " +
                          (wi.IsChild ? "[внутр. окно] " : "") + wi.Describe() +
                          " — исключение по дереву окна: " + r.Raw);
        }

        // Собрать все окна, совпадающие с правилами, в found (ключ = hwnd);
        // ruleOf — каким по счёту правилом найдено окно (для диспетчера правил).
        // С 1.2.3.3 исключения title!:/class!: проверяются по всему дереву
        // корневого окна: фраза не должна найтись нигде в окне, а не только
        // в совпавшем элементе (окно дозвона ловится иначе, чем фраза
        // «зарезерв» — они живут в разных элементах одной формы).
        public static void Collect(Settings s, Dictionary<long, WindowInfo> found,
            Dictionary<long, int> ruleOf)
        {
            VisibleOnly = s.VisibleOnly;
            List<WindowInfo> tops = new List<WindowInfo>();
            WinApi.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                WindowInfo wi = ReadWindow(h, false);
                if (wi != null) tops.Add(wi);
                return true;
            }, IntPtr.Zero);

            for (int ri = 0; ri < s.Rules.Count; ri++)
            {
                WatchRule r = s.Rules[ri];
                if (!r.Enabled) continue;
                string sc = Str.Lo(r.Scope);
                bool needTop = (sc == "top" || sc == "any");
                bool needChild = (sc == "child" || sc == "any");
                // проверять исключения по всему дереву окна (с 1.2.3.3)
                bool rootScan = !Str.IsEmpty(r.NotTitle) || !Str.IsEmpty(r.NotClass);

                if (needTop)
                {
                    for (int i = 0; i < tops.Count; i++)
                    {
                        WindowInfo wi = tops[i];
                        if (r.Matches(wi))
                        {
                            if (rootScan)
                            {
                                RootScan rs = ScanRootTop(wi);
                                if (rs.Rejects(r)) { NoteIgnored(r, wi, ri); continue; }
                            }
                            long k = wi.Hwnd.ToInt64();
                            if (!found.ContainsKey(k))
                            {
                                found[k] = wi;
                                if (ruleOf != null) ruleOf[k] = ri;
                            }
                        }
                    }
                }

                if (needChild)
                {
                    for (int i = 0; i < tops.Count; i++)
                    {
                        WindowInfo top = tops[i];
                        // оптимизация: если у правила указан proc и он не совпадает
                        // с процессом верхнего окна — дочерние можно не смотреть
                        if (!Str.IsEmpty(r.Proc) && !Str.ContainsIC(top.ProcessName, r.Proc)) continue;
                        // сначала собираем дочерние окна: список нужен и для
                        // скана исключений по всему дереву (с 1.2.3.3)
                        List<WindowInfo> children = new List<WindowInfo>();
                        WinApi.EnumChildWindows(top.Hwnd, delegate(IntPtr ch, IntPtr lp)
                        {
                            WindowInfo ci = ReadWindow(ch, true);
                            if (ci != null) { ci.RootHwnd = top.Hwnd; children.Add(ci); }
                            return true;
                        }, IntPtr.Zero);
                        RootScan rs = rootScan ? ScanRoot(top, children) : null;
                        for (int cj = 0; cj < children.Count; cj++)
                        {
                            WindowInfo ci = children[cj];
                            if (!r.Matches(ci)) continue;
                            if (rs != null && rs.Rejects(r)) { NoteIgnored(r, ci, ri); continue; }
                            long k = ci.Hwnd.ToInt64();
                            if (!found.ContainsKey(k))
                            {
                                found[k] = ci;
                                if (ruleOf != null) ruleOf[k] = ri;
                            }
                        }
                    }
                }
            }
        }


    }

    // ----------------------------------------------------------- CaptureOverlay
    // Плашка «окно под курсором» для режима захвата инспектора.
    // Клик сквозь неё проходит в нижележащее окно, поэтому выбор — Ctrl+Пробел.
    internal class CaptureOverlay : Form
    {
        public WindowInfo Result;
        private readonly Label _lbl;
        private readonly Timer _t;
        private bool _done;

        public CaptureOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(600, 78);
            BackColor = Color.FromArgb(24, 24, 24);
            Opacity = 0.94;

            _lbl = new Label();
            _lbl.Dock = DockStyle.Fill;
            _lbl.ForeColor = Color.White;
            _lbl.Font = new Font("Segoe UI", 9f);
            _lbl.TextAlign = ContentAlignment.MiddleLeft;
            _lbl.Padding = new Padding(12);
            _lbl.Text = "Наведите курсор на нужное окно...";
            Controls.Add(_lbl);

            _t = new Timer();
            _t.Interval = 25;
            _t.Tick += Poll;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT — мышь проходит насквозь
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW — без панели задач/alt-tab
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _t.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _t.Stop();
        }

        private void Poll(object sender, EventArgs e)
        {
            if (_done) return;
            try
            {
                if (WinApi.GetAsyncKeyState(0x1B) < 0) { Finish(null); return; } // Esc

                POINT p;
                if (!WinApi.GetCursorPos(out p)) return;
                // рабочая область того монитора, где курсор (с 1.2.3.6):
                // PrimaryScreen на мониторе слева/сверху от основного
                // (отрицательные координаты) утаскивал плашку на другой экран
                Rectangle wa = Screen.FromPoint(new Point(p.X, p.Y)).WorkingArea;
                int lx = p.X + 22;
                if (lx + Width > wa.Right) lx = p.X - Width - 10;
                int ly = p.Y + 22;
                if (ly + Height > wa.Bottom) ly = p.Y - Height - 10;
                Point np = new Point(Math.Max(lx, wa.Left), Math.Max(ly, wa.Top));
                if (Location != np) Location = np;   // присваиваем только при изменении

                IntPtr h = WinApi.WindowFromPoint(p);
                bool ctrl = WinApi.GetAsyncKeyState(0x11) < 0;
                IntPtr root = IntPtr.Zero;
                if (h != IntPtr.Zero) root = WinApi.GetAncestor(h, WinApi.GA_ROOT);
                bool isChild = ctrl && root != IntPtr.Zero && root != h;
                WindowInfo wi = Collector.ReadWindow(h, isChild, true);
                if (wi != null && wi.RootHwnd == IntPtr.Zero)
                    wi.RootHwnd = (root == IntPtr.Zero) ? h : root;

                string info = (wi == null) ? "нет окна"
                    : wi.ProcessName + "  |  " + wi.ClassName + "  |  " + Str.TrimTo(wi.Title, 44);
                _lbl.Text = "Под курсором: " + info + "\n" +
                            "Выбрать: Ctrl+Пробел    Отмена: Esc    (не кликайте мышью — клик уйдёт в окно!)";

                if (wi != null && ctrl && WinApi.GetAsyncKeyState(0x20) < 0) Finish(wi); // Ctrl+Пробел
            }
            catch { }
        }

        private void Finish(WindowInfo w)
        {
            if (_done) return;
            _done = true;
            Result = w;
            DialogResult = (w == null) ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }
    }

    // -------------------------------------------------------------- DetailsForm
    // Подробности выбранного окна + редактирование правила + сохранение в INI
    internal class DetailsForm : Form
    {
        private readonly WatchContext _owner;
        private readonly WindowInfo _w;

        public DetailsForm(WatchContext owner, WindowInfo w)
        {
            _owner = owner;
            _w = w;
            Icon = owner.AppIcon;

            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "Окно 0x" + w.Hwnd.ToInt64().ToString("X") + " — подробности";
            Size = new Size(680, 420);
            Font = new Font("Segoe UI", 9f);

            TextBox info = new TextBox();
            info.Multiline = true;
            info.ReadOnly = true;
            info.ScrollBars = ScrollBars.Vertical;
            info.BackColor = SystemColors.Window;
            info.Location = new Point(12, 10);
            info.Size = new Size(640, 130);
            info.Text =
                "hwnd:      0x" + w.Hwnd.ToInt64().ToString("X") + "\r\n" +
                "Класс:     " + w.ClassName + "\r\n" +
                "Заголовок: " + w.Title + "\r\n" +
                "Процесс:   " + w.ProcessName + "  (PID " + w.Pid + ")\r\n" +
                "Тип:       " + (w.IsChild ? "внутреннее окно" : "окно верхнего уровня") + "\r\n" +
                "Root hwnd: 0x" + w.RootHwnd.ToInt64().ToString("X");

            Label l = new Label();
            l.Text = "Черновик правила (дальше откроется понятный редактор):";
            l.AutoSize = true;
            l.Location = new Point(12, 150);

            TextBox rule = new TextBox();
            rule.Location = new Point(12, 170);
            rule.Size = new Size(640, 23);
            rule.Text = SuggestRule(w);

            Button add = new Button();
            add.Text = "Добавить в наблюдение...";
            add.Location = new Point(12, 205);
            add.Size = new Size(200, 28);
            add.Click += delegate
            {
                WatchRule seed = WatchRule.Parse(rule.Text) ?? WatchRule.FromWindow(_w);
                WatchRule res = RuleEditForm.Edit(this, seed, "Новое правило наблюдения", _owner.AppIcon);
                if (res == null) return;
                _owner.AddRule(res.ToRaw());
                Close();
            };

            Button copy = new Button();
            copy.Text = "Копировать правило";
            copy.Location = new Point(222, 205);
            copy.Size = new Size(160, 28);
            copy.Click += delegate { try { Clipboard.SetText(rule.Text); } catch { } };

            Button close = new Button();
            close.Text = "Закрыть";
            close.Location = new Point(392, 205);
            close.Size = new Size(100, 28);
            close.Click += delegate { Close(); };

            Label note = new Label();
            note.Location = new Point(12, 245);
            note.Size = new Size(640, 150);
            note.Text =
                "Подсказки:\r\n" +
                "•  Если в заголовке окна меняются цифры (номер телефона, ID) — удалите их из\r\n" +
                "   правила, оставив постоянную часть текста.\r\n" +
                "•  scope:top — окна верхнего уровня; scope:child — внутренние элементы окна;\r\n" +
                "   scope:any — искать везде.\r\n" +
                "•  Совпадение = все указанные поля найдены в окне, без учёта регистра.\r\n" +
                "•  Несколько правил (rule1, rule2, ...) работают как «ИЛИ».\r\n" +
                "•  Управлять правилами (вкл/выкл, изменить, удалить) — «Диспетчер\r\n" +
                "   правил...» в меню значка у часов.";

            Controls.Add(info);
            Controls.Add(l);
            Controls.Add(rule);
            Controls.Add(add);
            Controls.Add(copy);
            Controls.Add(close);
            Controls.Add(note);

            AcceptButton = add;
            CancelButton = close;
        }

        public static string SuggestRule(WindowInfo w)
        {
            StringBuilder sb = new StringBuilder();
            if (!Str.IsEmpty(w.ProcessName)) sb.Append("proc:").Append(w.ProcessName.Trim());
            string t = (w.Title == null) ? "" : w.Title.Trim();
            string c = (w.ClassName == null) ? "" : w.ClassName.Trim();
            if (t.Length > 0)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append("title:").Append(Str.Cut(t, 40));
            }
            else if (c.Length > 0)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append("class:").Append(c);
            }
            sb.Append(";scope:").Append(w.IsChild ? "child" : "top");
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------ InspectorForm
    // Список всех окон верхнего уровня + режим захвата «окно под курсором»
    internal class InspectorForm : Form
    {
        private readonly WatchContext _owner;
        private readonly SmoothListView _list;
        private readonly TextBox _filter;
        private readonly CheckBox _auto;
        private readonly Label _status;
        private readonly Timer _t;
        private readonly RowSorter _sorter = new RowSorter();
        private string _lastSig;

        public InspectorForm(WatchContext owner)
        {
            _owner = owner;
            Icon = owner.AppIcon;

            Text = "Oktell Watch — инспектор окон";
            Size = new Size(920, 580);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 70;
            top.Padding = new Padding(8);

            Label lblF = new Label();
            lblF.Text = "Фильтр:";
            lblF.AutoSize = true;
            lblF.Location = new Point(10, 12);
            top.Controls.Add(lblF);

            _filter = new TextBox();
            _filter.Location = new Point(62, 9);
            _filter.Width = 220;
            _filter.TextChanged += delegate { RefreshList(); };
            top.Controls.Add(_filter);

            Button btnRefresh = new Button();
            btnRefresh.Text = "Обновить";
            btnRefresh.Location = new Point(292, 7);
            btnRefresh.Size = new Size(90, 25);
            btnRefresh.Click += delegate { RefreshList(); };
            top.Controls.Add(btnRefresh);

            _auto = new CheckBox();
            _auto.Text = "Автообновление";
            _auto.Location = new Point(392, 10);
            _auto.AutoSize = true;
            _auto.Checked = true;
            top.Controls.Add(_auto);

            Button btnCapture = new Button();
            btnCapture.Text = "Окно под курсором...";
            btnCapture.Location = new Point(10, 38);
            btnCapture.Size = new Size(170, 26);
            btnCapture.Click += delegate { StartCapture(); };
            top.Controls.Add(btnCapture);

            Button btnRules = new Button();
            btnRules.Text = "Диспетчер правил...";
            btnRules.Location = new Point(190, 38);
            btnRules.Size = new Size(150, 26);
            btnRules.Click += delegate { _owner.ShowRulesManager(); };
            top.Controls.Add(btnRules);

            Label hint = new Label();
            hint.Text = "Двойной клик по строке — подробности и добавление правила";
            hint.AutoSize = true;
            hint.Location = new Point(352, 43);
            top.Controls.Add(hint);

            _list = new SmoothListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.Columns.Add("hwnd", 90);
            _list.Columns.Add("Класс окна", 230);
            _list.Columns.Add("Заголовок окна", 340);
            _list.Columns.Add("Процесс", 140);
            _list.Columns.Add("PID", 60);
            _list.ListViewItemSorter = _sorter;
            _list.ColumnClick += delegate(object s, ColumnClickEventArgs e)
            {
                if (_sorter.Col == e.Column) _sorter.Asc = !_sorter.Asc;
                else { _sorter.Col = e.Column; _sorter.Asc = true; }
                _list.Sort();
            };
            _list.DoubleClick += delegate { ShowDetails(); };

            _status = new Label();
            _status.Dock = DockStyle.Bottom;
            _status.Height = 22;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(8, 3, 2, 2);
            _status.ForeColor = SystemColors.GrayText;

            Controls.Add(_list);
            Controls.Add(top);
            Controls.Add(_status);

            _t = new Timer();
            _t.Interval = Math.Max(500, Math.Min(10000, _owner.Cfg.ListRefreshMs));
            _t.Tick += delegate { if (_auto.Checked) RefreshList(); };

            Load += delegate { RefreshList(); _t.Start(); };
            FormClosed += delegate { _t.Stop(); };
        }

        private void RefreshList()
        {
            try
            {
                List<WindowInfo> ws = new List<WindowInfo>();
                WinApi.EnumWindows(delegate(IntPtr h, IntPtr lp)
                {
                    WindowInfo wi = Collector.ReadWindow(h, false, true); // свои окна тоже показываем
                    if (wi != null) ws.Add(wi);
                    return true;
                }, IntPtr.Zero);

                string f = Str.Lo(_filter.Text);

                // Сигнатура видимого набора: если ничего не изменилось, список
                // не трогаем вовсе — окно не мигает и не перерисовывается
                StringBuilder sig = new StringBuilder();
                for (int i = 0; i < ws.Count; i++)
                {
                    WindowInfo wi = ws[i];
                    if (!FilterMatch(wi, f)) continue;
                    sig.Append(wi.Hwnd.ToInt64().ToString("X")).Append('\u0001')
                       .Append(wi.ClassName).Append('\u0001')
                       .Append(wi.Title).Append('\u0001')
                       .Append(wi.ProcessName).Append('\u0001')
                       .Append(wi.Pid).Append('\u0002');
                }
                string signature = sig.ToString();
                if (signature == _lastSig)
                {
                    _status.Text = StatusText(ws.Count);
                    return;
                }
                _lastSig = signature;

                // diff-обновление строк по hwnd: новые добавляются, исчезнувшие
                // удаляются, у оставшихся правится текст — без полной пересборки
                Dictionary<long, ListViewItem> old = new Dictionary<long, ListViewItem>();
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    WindowInfo w2 = _list.Items[i].Tag as WindowInfo;
                    if (w2 != null) old[w2.Hwnd.ToInt64()] = _list.Items[i];
                }

                _list.BeginUpdate();
                for (int i = 0; i < ws.Count; i++)
                {
                    WindowInfo wi = ws[i];
                    if (!FilterMatch(wi, f)) continue;

                    ListViewItem it;
                    if (!old.TryGetValue(wi.Hwnd.ToInt64(), out it))
                    {
                        it = new ListViewItem("");
                        it.SubItems.Add("");
                        it.SubItems.Add("");
                        it.SubItems.Add("");
                        it.SubItems.Add("");
                        _list.Items.Add(it);
                    }
                    else
                    {
                        old.Remove(wi.Hwnd.ToInt64());
                    }
                    it.Text = wi.Hwnd.ToInt64().ToString("X");
                    it.SubItems[1].Text = wi.ClassName;
                    it.SubItems[2].Text = wi.Title;
                    it.SubItems[3].Text = wi.Pid == Collector.OwnPid
                        ? wi.ProcessName + "  (это окно программы)" : wi.ProcessName;
                    it.SubItems[4].Text = wi.Pid.ToString();
                    it.Tag = wi;
                }
                foreach (KeyValuePair<long, ListViewItem> kv in old) _list.Items.Remove(kv.Value);
                _list.EndUpdate();
                _list.Sort();

                _status.Text = StatusText(ws.Count);
            }
            catch { }
        }

        // Фильтр инспектора: подстрока в заголовке, классе, процессе или
        // в hwnd (шестнадцатеричный вид, как в столбце «hwnd»)
        private static bool FilterMatch(WindowInfo wi, string f)
        {
            if (f.Length == 0) return true;
            if (Str.Lo(wi.Title).Contains(f)) return true;
            if (Str.Lo(wi.ClassName).Contains(f)) return true;
            if (Str.Lo(wi.ProcessName).Contains(f)) return true;
            return wi.Hwnd.ToInt64().ToString("X").ToLowerInvariant().Contains(f);
        }

        private string StatusText(int total)
        {
            return "Окон: " + _list.Items.Count + " из " + total +
                   "   •   обновление раз в " + _t.Interval + " мс" +
                   "   •   клик по заголовку столбца — сортировка";
        }

        // Сортировка по клику на заголовок столбца
        private class RowSorter : System.Collections.IComparer
        {
            public int Col = 3;   // по умолчанию — по процессу
            public bool Asc = true;

            public int Compare(object a, object b)
            {
                ListViewItem x = a as ListViewItem;
                ListViewItem y = b as ListViewItem;
                if (x == null || y == null) return 0;
                int r;
                if (Col == 0)
                {
                    long nx = ParseHex(x.SubItems[0].Text), ny = ParseHex(y.SubItems[0].Text);
                    r = nx.CompareTo(ny);
                }
                else if (Col == 4)
                {
                    int nx, ny;
                    int.TryParse(x.SubItems[4].Text, out nx);
                    int.TryParse(y.SubItems[4].Text, out ny);
                    r = nx.CompareTo(ny);
                }
                else
                {
                    r = String.Compare(x.SubItems[Col].Text, y.SubItems[Col].Text,
                        StringComparison.OrdinalIgnoreCase);
                }
                return Asc ? r : -r;
            }

            private static long ParseHex(string s)
            {
                long v;
                long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
                return v;
            }
        }

        private void ShowDetails()
        {
            try
            {
                if (_list.SelectedItems.Count == 0) return;
                WindowInfo wi = _list.SelectedItems[0].Tag as WindowInfo;
                if (wi == null) return;
                DetailsForm df = new DetailsForm(_owner, wi);
                df.ShowDialog(this);
            }
            catch { }
        }

        private void StartCapture()
        {
            _owner.SetCaptureHold(true);
            Hide();
            WindowInfo res = null;
            try
            {
                CaptureOverlay ov = new CaptureOverlay();
                ov.Location = new Point(100, 100);
                ov.ShowDialog();
                res = ov.Result;
                ov.Dispose();
            }
            catch { }
            _owner.SetCaptureHold(false);
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            if (res != null)
            {
                RefreshList();
                try
                {
                    DetailsForm df = new DetailsForm(_owner, res);
                    df.ShowDialog(this);
                }
                catch { }
            }
        }
    }

    // ----------------------------------------------------------------- HelpForm
    internal class HelpForm : Form
    {
        private static HelpForm _inst;

        public static void ShowHelp()
        {
            try
            {
                if (_inst == null || _inst.IsDisposed)
                {
                    _inst = new HelpForm();
                    _inst.Show();
                }
                else
                {
                    _inst.Show();
                    _inst.Activate();
                }
            }
            catch { }
        }

        private HelpForm()
        {
            Text = "Oktell Watch — справка";
            Size = new Size(780, 660);
            StartPosition = FormStartPosition.CenterScreen;

            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Vertical;
            tb.Dock = DockStyle.Fill;
            tb.Font = new Font("Consolas", 9.5f);
            tb.BackColor = SystemColors.Window;
            tb.Text = HelpText.Replace("\n", Environment.NewLine);
            Controls.Add(tb);
        }

        public const string HelpText =
@"OKTELL WATCH 1.2.3 — сторож формы обратного звонка
============================================================

ЗАЧЕМ
Штатная форма задачи обратного звонка Oktell появляется тихо и часто
остаётся на заднем плане — оператор её не видит и не слышит. Программа
мгновенно ловит показ нового окна (события Windows) и, найдя форму
по правилам, подаёт звук, выводит окно Oktell наверх и мигает кнопкой
на панели задач, пока форма не будет обработана. Раз в 15 секунд
выполняется страховочный полный опрос всех окон.

КАК ПОДКЛЮЧИТЬ НУЖНОЕ ОКНО (когда стандартное правило не срабатывает)
1. Спровоцируйте появление формы обратного звонка (тестовая задача).
2. Правый клик по значку в трее -> «Инспектор окон...».
3. Нажмите «Окно под курсором...». Инспектор спрячется.
4. Наведите мышь на форму Oktell — на плашке возле курсора появятся
   данные окна. Выберите окно: Ctrl + Пробел. Esc — отмена.
   ВНИМАНИЕ: не кликайте мышью по форме — клик пройдёт в окно под
   курсором и может нажать кнопку («Принять» / «Удалить»).
5. Проверьте данные окна и нажмите «Добавить в наблюдение...» —
   откроется редактор правила (поля: процесс, заголовок, класс,
   исключения). Правило сохранится и заработает сразу — перезапуск
   не нужен.

ДИСПЕТЧЕР ПРАВИЛ (править config.ini не нужно!)
Меню значка в трее -> «Диспетчер правил...».
•  Галочка = правило активно. Сняли — правило отключено (остаётся
   в config.ini с пометкой off:true).
•  «Добавить» / «Изменить» — редактор с полями: процесс, заголовок,
   класс окна, исключения («НЕТ текста/класса»), где искать.
   Признак и исключение — разные поля, их можно сочетать:
   заголовок ~«зарезерв» + в окне НЕТ ~«дозвон».
•  «Удалить» — убрать правило совсем (с подтверждением).
•  Столбец «Окон сейчас» показывает, сколько окон ловит каждое
   правило прямо сейчас — сразу видно слишком широкое правило.
•  Вкладка «Окна в работе» — формы, по которым идёт оповещение
   (двойной клик — перейти к правилу, которое их поймало).
•  Клавиши: F2 — изменить, Delete — удалить.

ФОРМАТ ПРАВИЛ (config.ini, секция [watch] — для админов)
   rule1=proc:oktell;title:обратн;scope:top
   proc:   часть имени процесса (например oktell)
   title:  часть заголовка окна
   class:  часть класса окна
   scope:  top   — окна верхнего уровня (по умолчанию)
           child — внутренние окна (элементы внутри главного окна)
           any   — искать и там, и там
   proc!:  процесс НЕ должен содержать (исключение, с 1.2)
   title!: фраза НЕ должна найтись нигде в окне (исключение, с 1.2)
   class!: класс НЕ должен найтись нигде в окне (исключение, с 1.2)
• Регистр не важен; совпадение = ВСЕ указанные поля найдены в окне.
• Исключения (со знаком «!») сужают правило: окно подходит, только
  если исключающая подстрока НЕ найдена нигде в окне — в заголовке
  и во внутренних элементах (с 1.2.3.3). Например, ловим «обратн»,
  но не окна дозвона после подтверждения:
   rule1=proc:oktell;title:обратн;title!:дозвон;scope:top
  Хотя бы один обычный признак (без «!») обязателен.
• Несколько правил (rule1, rule2, ...) работают как «ИЛИ».
• Выключенное правило хранится с пометкой off:true — ставит и снимает
  его Диспетчер правил (галочкой).
• Если в заголовке меняются цифры (номер, ID) — оставьте постоянную
  часть текста, цифры уберите.

СИГНАЛЫ
Звук         — задан в [alert] soundFile (если файла нет — системный).
Повтор       — каждые repeatEverySec секунд, пока форма не обработана.
Обрыв        — закрыли последнюю наблюдаемую форму — звук гаснет
               мгновенно и не доигрывает до конца файла (чтобы сигнал
               не попал в эфир, когда оператор уже говорит с клиентом).
               Закрытие всей формы гасит звук и тогда, когда отслеживался
               её внутренний элемент (с 1.2.3.5). Пауза наблюдения глушит
               звук тоже.
Окно наверх  — при первом обнаружении (галочка «Поднимать окно наверх»
               в меню трея, либо bringToFront=false в config.ini).
Мигание      — кнопка окна мигает на панели задач до его активации.

ЖУРНАЛ
Журнал: system\logs рядом с программой, файл OktellWatch_ГГГГММДД.log
(папку можно поменять: [log] folder= в config.ini).
События: START, DETECTED (форма найдена), ALERT / RE-ALERT (сигналы),
CLOSED (форма обработана/закрыта), SOUND (звук остановлен), CONFIG
(изменения настроек), IGNORED (окно пропущено исключением правила),
PAUSE / RESUME, ERROR.

АВТОЗАПУСК
Меню значка в трее -> «Автозапуск с Windows» (прописывается в реестр
текущего пользователя). Либо ярлык в папке shell:startup.

НАСТРОЙКА НА ХОДУ
config.ini можно править в Блокноте прямо во время работы — программа
перечитает его в течение секунды. Сохраняйте файл в кодировке UTF-8.
После значения обычной настройки можно писать пояснение через « ; »
(пример: pollIntervalMs=15000 ; раз в 15 секунд). В строках rule*
«;» — разделитель полей правила: пояснения — отдельной строкой выше.

ПАУЗА
Меню значка -> «Пауза наблюдения» — сигналы отключаются, играющий
звук обрывается, журнал продолжает вестись.

СОВЕТ ПО ВНЕДРЕНИЮ
Сначала обкатайте на одном компьютере: найдите инспектором реальное
окно формы, уточните правило, послушайте громкость сигнала, затем
включите автозапуск и разверните на остальных АРМ (достаточно одного
файла OktellWatch.exe — конфиг и журнал в подпапке system создадутся
сами).
";
    }

    // -------------------------------------------------------------- TrackView
    // Снимок отслеживаемого окна — для диспетчера правил
    internal class TrackView
    {
        public long Hwnd;
        public string Proc;
        public string Title;
        public bool IsChild;
        public int RuleNo;
        public int Alerts;
        public DateTime FirstSeen;
    }

    // ---------------------------------------------------------- SmoothListView
    // ListView с двойной буферизацией — не мигает при обновлении
    internal class SmoothListView : ListView
    {
        public SmoothListView() { DoubleBuffered = true; }
    }

    // ------------------------------------------------------------- RuleEditForm
    // Понятный редактор правила: поля вместо синтаксиса config.ini.
    // Признаки и исключения — отдельные поля: признак и исключение можно
    // сочетать (например, «заголовок ~зарезерв» + «в окне НЕТ дозвон»).
    internal class RuleEditForm : Form
    {
        private readonly TextBox _tbProc;
        private readonly TextBox _tbTitle;
        private readonly TextBox _tbClass;
        private readonly TextBox _tbNotProc;
        private readonly TextBox _tbNotTitle;
        private readonly TextBox _tbNotClass;
        private readonly ComboBox _cbScope;
        private readonly Label _lblPreview;
        private readonly bool _wasEnabled;

        public string SavedRaw;

        public RuleEditForm(WatchRule seed, string caption)
        {
            _wasEnabled = seed == null || seed.Enabled;

            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            // Подпись расположена НАД полем ввода и не налезает на него ни при
            // каком DPI (исправлено в 1.1.1). Примеры показываются внутри
            // пустых полей — см. OnShown.
            int y = 14;

            // Признаки — то, что ДОЛЖНО найтись в окне (хотя бы одно поле)
            AddSection("Признаки окна (хотя бы один):", ref y);
            _tbProc = AddField("Процесс содержит:",
                seed == null ? "" : Clean(seed.Proc), ref y);
            _tbTitle = AddField("Заголовок окна содержит:",
                seed == null ? "" : Clean(seed.Title), ref y);
            _tbClass = AddField("Класс окна содержит:",
                seed == null ? "" : Clean(seed.Class), ref y);

            // Исключения — то, чего в окне быть НЕ должно; у каждого условия
            // своё поле, поэтому признак и исключение можно сочетать:
            // заголовок ~«зарезерв» + в окне НЕТ «дозвон» (с 1.2.3.4)
            AddSection("Исключения — правило молчит, если найдётся:", ref y);
            _tbNotProc = AddField("Процесс НЕ содержит:",
                seed == null ? "" : Clean(seed.NotProc), ref y);
            _tbNotTitle = AddField("В окне НЕТ текста:",
                seed == null ? "" : Clean(seed.NotTitle), ref y);
            _tbNotClass = AddField("Класса НЕТ в окне:",
                seed == null ? "" : Clean(seed.NotClass), ref y);

            Label l4 = new Label();
            l4.Text = "Где искать:";
            l4.Location = new Point(14, y);
            l4.AutoSize = true;
            Controls.Add(l4);

            _cbScope = new ComboBox();
            _cbScope.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbScope.Width = 442;
            _cbScope.Items.Add("Только главные окна (top)");
            _cbScope.Items.Add("Главные и вложенные окна (any)");
            _cbScope.Items.Add("Только вложенные окна (child)");
            string sc = seed == null ? "top" : Str.Lo(seed.Scope);
            _cbScope.SelectedIndex = sc == "any" ? 1 : (sc == "child" ? 2 : 0);
            _cbScope.SelectedIndexChanged += delegate { UpdatePreview(); };
            Controls.Add(_cbScope);
            _cbScope.Location = new Point(14, y + l4.Height + 3);
            y += l4.Height + _cbScope.Height + 10;

            _lblPreview = new Label();
            _lblPreview.Location = new Point(14, y);
            _lblPreview.Size = new Size(442, 32);
            _lblPreview.ForeColor = SystemColors.HotTrack;
            Controls.Add(_lblPreview);
            y += 40;

            Button ok = new Button();
            ok.Text = "Сохранить правило";
            ok.Location = new Point(14, y);
            ok.Size = new Size(170, 30);
            ok.Click += delegate { SaveRule(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Отмена";
            cancel.Location = new Point(196, y);
            cancel.Size = new Size(100, 30);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            // Высота формы — по фактическому содержимому (зависит от шрифта/DPI)
            ClientSize = new Size(470, y + 44);

            _tbProc.TextChanged += delegate { UpdatePreview(); };
            _tbTitle.TextChanged += delegate { UpdatePreview(); };
            _tbClass.TextChanged += delegate { UpdatePreview(); };
            _tbNotProc.TextChanged += delegate { UpdatePreview(); };
            _tbNotTitle.TextChanged += delegate { UpdatePreview(); };
            _tbNotClass.TextChanged += delegate { UpdatePreview(); };
            UpdatePreview();
        }

        // Подпись раздела серым — объединяет поля в группы признаков/исключений
        private void AddSection(string caption, ref int y)
        {
            Label s = new Label();
            s.Text = caption;
            s.AutoSize = true;
            s.ForeColor = SystemColors.GrayText;
            s.Location = new Point(14, y + 2);
            Controls.Add(s);
            y += s.Height + 6;
        }

        // Поле ввода: подпись над полем (не налезает на поле ни при каком DPI)
        private TextBox AddField(string caption, string value, ref int y)
        {
            Label l = new Label();
            l.Text = caption;
            l.Location = new Point(14, y);
            l.AutoSize = true;
            Controls.Add(l);

            TextBox tb = new TextBox();
            tb.Text = value;
            Controls.Add(tb);
            tb.Location = new Point(14, y + l.Height + 3);
            tb.Width = 442;

            y += l.Height + tb.Height + 10;
            return tb;
        }

        // Подсказки-примеры внутри пустых полей (исчезают при вводе)
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            WinApi.SetCue(_tbProc, "например: oktell");
            WinApi.SetCue(_tbTitle, "например: перезвон");
            WinApi.SetCue(_tbClass, "необязательно, можно оставить пустым");
            WinApi.SetCue(_tbNotProc, "например: тест");
            WinApi.SetCue(_tbNotTitle, "например: дозвон");
            WinApi.SetCue(_tbNotClass, "необязательно");
        }

        // Точка с запятой — разделитель полей правила, в значениях её быть не должно
        private static string Clean(string s)
        {
            if (String.IsNullOrEmpty(s)) return "";
            return s.Replace(";", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private string ScopeValue()
        {
            switch (_cbScope.SelectedIndex)
            {
                case 1: return "any";
                case 2: return "child";
                default: return "top";
            }
        }

        private WatchRule Build()
        {
            string proc = Clean(_tbProc.Text);
            string title = Clean(_tbTitle.Text);
            string cls = Clean(_tbClass.Text);
            string notProc = Clean(_tbNotProc.Text);
            string notTitle = Clean(_tbNotTitle.Text);
            string notClass = Clean(_tbNotClass.Text);
            // хотя бы один ОБЫЧНЫЙ признак обязателен: правило только из
            // исключений ловило бы вообще все окна
            if (proc.Length == 0 && title.Length == 0 && cls.Length == 0) return null;
            StringBuilder sb = new StringBuilder();
            if (!_wasEnabled) sb.Append("off:true;");
            if (proc.Length > 0) sb.Append("proc:").Append(proc).Append(';');
            if (title.Length > 0) sb.Append("title:").Append(title).Append(';');
            if (cls.Length > 0) sb.Append("class:").Append(cls).Append(';');
            if (notProc.Length > 0) sb.Append("proc!:").Append(notProc).Append(';');
            if (notTitle.Length > 0) sb.Append("title!:").Append(notTitle).Append(';');
            if (notClass.Length > 0) sb.Append("class!:").Append(notClass).Append(';');
            sb.Append("scope:").Append(ScopeValue());
            return WatchRule.Parse(sb.ToString());
        }

        private void UpdatePreview()
        {
            WatchRule r = Build();
            _lblPreview.Text = r == null
                ? "Укажите хотя бы один признак окна"
                : "В config.ini:  " + r.ToRaw();
        }

        private void SaveRule()
        {
            WatchRule r = Build();
            if (r == null)
            {
                MessageBox.Show("Укажите хотя бы один признак: процесс, заголовок или класс окна.\n" +
                        "Исключения («НЕТ текста/класса») только сужают правило — сами по себе признаком не являются.",
                    "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SavedRaw = r.ToRaw();
            DialogResult = DialogResult.OK;
            Close();
        }

        // Открыть редактор; вернуть готовое правило (null = отмена)
        public static WatchRule Edit(IWin32Window owner, WatchRule seed, string caption, Icon icon)
        {
            using (RuleEditForm f = new RuleEditForm(seed, caption))
            {
                f.Icon = icon;
                if (f.ShowDialog(owner) != DialogResult.OK) return null;
                return WatchRule.Parse(f.SavedRaw);
            }
        }
    }

    // --------------------------------------------------------- RulesManagerForm
    // Диспетчер правил: включение/отключение галочками, добавление, изменение
    // и удаление правил без правки config.ini. Вкладка «Окна в работе»
    // показывает формы, по которым сейчас идёт оповещение.
    internal class RulesManagerForm : Form
    {
        private readonly WatchContext _owner;
        private readonly TabControl _tabs;
        private readonly SmoothListView _lvRules;
        private readonly SmoothListView _lvLive;
        private readonly Timer _t;
        private List<WatchRule> _rules = new List<WatchRule>();
        private bool _loading;
        private string _liveSig = "";

        public RulesManagerForm(WatchContext owner)
        {
            _owner = owner;
            Icon = owner.AppIcon;

            Text = "Oktell Watch — диспетчер правил";
            Size = new Size(780, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;

            // --- вкладка «Правила наблюдения» ---
            TabPage tpRules = new TabPage("Правила наблюдения");

            _lvRules = new SmoothListView();
            _lvRules.Dock = DockStyle.Fill;
            _lvRules.View = View.Details;
            _lvRules.FullRowSelect = true;
            _lvRules.MultiSelect = false;
            _lvRules.HideSelection = false;
            _lvRules.CheckBoxes = true;
            _lvRules.Columns.Add("№", 36);
            _lvRules.Columns.Add("Что ищем", 330);
            _lvRules.Columns.Add("Правило (config.ini)", 240);
            _lvRules.Columns.Add("Окон сейчас", 84);
            _lvRules.ItemChecked += RuleChecked;
            _lvRules.DoubleClick += delegate { EditSelected(); };
            _lvRules.KeyDown += ListKeyDown;

            Panel pb = new Panel();
            pb.Dock = DockStyle.Bottom;
            pb.Height = 62;

            Button btnAdd = new Button();
            btnAdd.Text = "Добавить";
            btnAdd.Location = new Point(10, 8);
            btnAdd.Size = new Size(110, 28);
            btnAdd.Click += delegate { AddNew(); };
            pb.Controls.Add(btnAdd);

            Button btnEdit = new Button();
            btnEdit.Text = "Изменить";
            btnEdit.Location = new Point(128, 8);
            btnEdit.Size = new Size(110, 28);
            btnEdit.Click += delegate { EditSelected(); };
            pb.Controls.Add(btnEdit);

            Button btnDel = new Button();
            btnDel.Text = "Удалить";
            btnDel.Location = new Point(246, 8);
            btnDel.Size = new Size(110, 28);
            btnDel.Click += delegate { DeleteSelected(); };
            pb.Controls.Add(btnDel);

            Button btnReload = new Button();
            btnReload.Text = "Перечитать config.ini";
            btnReload.Location = new Point(364, 8);
            btnReload.Size = new Size(170, 28);
            btnReload.Click += delegate { Populate(); };
            pb.Controls.Add(btnReload);

            Label hint = new Label();
            hint.Text = "Галочка — правило активно. F2 — изменить, Delete — удалить.";
            hint.AutoSize = true;
            hint.ForeColor = SystemColors.GrayText;
            hint.Location = new Point(10, 40);
            pb.Controls.Add(hint);

            tpRules.Controls.Add(_lvRules);
            tpRules.Controls.Add(pb);

            // --- вкладка «Окна в работе» ---
            TabPage tpLive = new TabPage("Окна в работе");

            _lvLive = new SmoothListView();
            _lvLive.Dock = DockStyle.Fill;
            _lvLive.View = View.Details;
            _lvLive.FullRowSelect = true;
            _lvLive.MultiSelect = false;
            _lvLive.HideSelection = false;
            _lvLive.Columns.Add("hwnd", 90);
            _lvLive.Columns.Add("Процесс", 130);
            _lvLive.Columns.Add("Заголовок окна", 320);
            _lvLive.Columns.Add("Правило", 64);
            _lvLive.Columns.Add("Сигналов", 70);
            _lvLive.Columns.Add("Найдено в", 76);
            _lvLive.DoubleClick += delegate { FindRuleOfLive(); };

            Panel lb = new Panel();
            lb.Dock = DockStyle.Bottom;
            lb.Height = 44;

            Button btnFind = new Button();
            btnFind.Text = "Показать правило";
            btnFind.Location = new Point(10, 8);
            btnFind.Size = new Size(150, 28);
            btnFind.Click += delegate { FindRuleOfLive(); };
            lb.Controls.Add(btnFind);

            Label lh = new Label();
            lh.Text = "Окна, по которым сейчас идёт оповещение. Двойной клик — перейти к правилу.";
            lh.AutoSize = true;
            lh.ForeColor = SystemColors.GrayText;
            lh.Location = new Point(170, 14);
            lb.Controls.Add(lh);

            tpLive.Controls.Add(_lvLive);
            tpLive.Controls.Add(lb);

            _tabs.TabPages.Add(tpRules);
            _tabs.TabPages.Add(tpLive);
            Controls.Add(_tabs);

            _t = new Timer();
            _t.Interval = Math.Max(500, Math.Min(5000, _owner.Cfg.ListRefreshMs));
            _t.Tick += delegate { if (Visible) UpdateCounts(); };

            Load += delegate { Populate(); _t.Start(); };
            FormClosed += delegate { _t.Stop(); };
        }

        private void Populate()
        {
            try
            {
                _loading = true;
                _rules = _owner.CurrentRules();
                _lvRules.BeginUpdate();
                _lvRules.Items.Clear();
                for (int i = 0; i < _rules.Count; i++)
                {
                    WatchRule r = _rules[i];
                    ListViewItem it = new ListViewItem((i + 1).ToString());
                    it.SubItems.Add((r.Enabled ? "" : "(ВЫКЛ) ") + r.DescribeFriendly());
                    it.SubItems.Add(r.ToRaw());
                    it.SubItems.Add("—");
                    it.Checked = r.Enabled;
                    it.ForeColor = r.Enabled ? SystemColors.WindowText : SystemColors.GrayText;
                    it.Tag = r;
                    _lvRules.Items.Add(it);
                }
                _lvRules.EndUpdate();
                _loading = false;
                UpdateCounts();
            }
            catch { _loading = false; }
        }

        private void RuleChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_loading) return;
            WatchRule r = e.Item.Tag as WatchRule;
            if (r == null || r.Enabled == e.Item.Checked) return;
            r.Enabled = e.Item.Checked;
            e.Item.ForeColor = r.Enabled ? SystemColors.WindowText : SystemColors.GrayText;
            e.Item.SubItems[1].Text = (r.Enabled ? "" : "(ВЫКЛ) ") + r.DescribeFriendly();
            e.Item.SubItems[2].Text = r.ToRaw();
            _owner.SaveRules(_rules);
        }

        private void AddNew()
        {
            WatchRule res = RuleEditForm.Edit(this, null, "Новое правило наблюдения", _owner.AppIcon);
            if (res == null) return;
            _rules.Add(res);
            if (_owner.SaveRules(_rules))
            {
                Populate();
                SelectRule(_rules.Count - 1);
            }
            else
            {
                _rules.RemoveAt(_rules.Count - 1);
            }
        }

        private void EditSelected()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            int idx = _lvRules.SelectedIndices[0];
            if (idx < 0 || idx >= _rules.Count) return;
            WatchRule res = RuleEditForm.Edit(this, _rules[idx],
                "Правило №" + (idx + 1) + " — изменение", _owner.AppIcon);
            if (res == null) return;
            _rules[idx] = res;
            if (_owner.SaveRules(_rules)) Populate();
        }

        private void DeleteSelected()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            int idx = _lvRules.SelectedIndices[0];
            if (idx < 0 || idx >= _rules.Count) return;
            DialogResult dr = MessageBox.Show(
                "Удалить правило №" + (idx + 1) + "?\n\n" + _rules[idx].DescribeFriendly(),
                "Oktell Watch", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;
            _rules.RemoveAt(idx);
            if (_owner.SaveRules(_rules)) Populate();
        }

        private void SelectRule(int idx)
        {
            if (idx < 0 || idx >= _lvRules.Items.Count) return;
            _tabs.SelectedIndex = 0;
            _lvRules.Items[idx].Selected = true;
            _lvRules.Items[idx].Focused = true;
            _lvRules.EnsureVisible(idx);
        }

        private void ListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.F2) { EditSelected(); e.Handled = true; }
        }

        private void UpdateCounts()
        {
            try
            {
                int[] counts = _owner.MatchCounts;
                for (int i = 0; i < _lvRules.Items.Count; i++)
                    _lvRules.Items[i].SubItems[3].Text =
                        (counts != null && i < counts.Length) ? counts[i].ToString() : "—";
                RefreshLive();
            }
            catch { }
        }

        private void RefreshLive()
        {
            List<TrackView> ts = _owner.SnapshotTracks();
            StringBuilder sig = new StringBuilder();
            for (int i = 0; i < ts.Count; i++)
                sig.Append(ts[i].Hwnd.ToString()).Append('|').Append(ts[i].Alerts)
                   .Append('|').Append(ts[i].RuleNo).Append('\u0002');
            string s = sig.ToString();
            if (s == _liveSig) return;
            _liveSig = s;

            _lvLive.BeginUpdate();
            _lvLive.Items.Clear();
            for (int i = 0; i < ts.Count; i++)
            {
                TrackView v = ts[i];
                ListViewItem it = new ListViewItem(v.Hwnd.ToString("X"));
                it.SubItems.Add(v.Proc);
                it.SubItems.Add((v.IsChild ? "[внутр.] " : "") + v.Title);
                it.SubItems.Add(v.RuleNo > 0 ? "№" + v.RuleNo : "—");
                it.SubItems.Add(v.Alerts.ToString());
                it.SubItems.Add(v.FirstSeen.ToString("HH:mm:ss"));
                _lvLive.Items.Add(it);
            }
            _lvLive.EndUpdate();
        }

        private void FindRuleOfLive()
        {
            if (_lvLive.SelectedItems.Count == 0) return;
            int ruleNo;
            int.TryParse(_lvLive.SelectedItems[0].SubItems[3].Text.Replace("№", ""), out ruleNo);
            if (ruleNo > 0) SelectRule(ruleNo - 1);
        }
    }

    // ------------------------------------------------------------- WatchContext
    internal class WatchContext : ApplicationContext
    {
        private class Track
        {
            public WindowInfo Info;
            public DateTime FirstSeen;
            public DateTime LastAlert;
            public int Alerts;
            public bool Alerted;
            public int RuleNo;   // каким правилом поймано (для диспетчера), 1-based
        }

        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValueName = "OktellWatch";

        private readonly Settings _settings;
        private readonly NotifyIcon _tray;
        private readonly Timer _timer;
        private readonly Dictionary<long, Track> _tracked = new Dictionary<long, Track>();
        private ToolStripMenuItem _miStatus;
        private ToolStripMenuItem _miPause;
        private ToolStripMenuItem _miFront;
        private ToolStripMenuItem _miAuto;
        private SoundPlayer _player;
        private string _playerFile;
        private bool _paused;
        private bool _captureHold;
        private InspectorForm _inspector;
        private RulesManagerForm _manager;
        private IntPtr _hook = IntPtr.Zero;
        private WinApi.WinEventDelegate _hookProc;
        private int _lastScanTick = int.MinValue;
        private int _lastCfgTick = int.MinValue;   // троттлинг проверки конфига в hook-пути
        private int _hookCooldownMs = 250;
        private Timer _tailTimer;                  // «хвост» троттлинга скана (с 1.2.3.6)
        private int[] _matchCounts = new int[0];

        // -------------------------------------- portable-компоновка (system\)
        // Служебные файлы живут в подпапке system рядом с EXE: config.ini и
        // журнал (по умолчанию system\logs), в пакете из Releases — ещё
        // build.bat и значок. Рядом с EXE остаётся только сам EXE — не нужно
        // гадать, какой файл запускать. Если конфига в system ещё нет, а в
        // корне лежит config.ini от версии 1.2 и раньше — он копируется в
        // system (старый файл остаётся на месте: откат полностью безопасен).
        private bool _configMigrated;

        private string ResolveConfigPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string sysDir = Path.Combine(baseDir, "system");
            string newPath = Path.Combine(sysDir, "config.ini");
            string oldPath = Path.Combine(baseDir, "config.ini");
            try
            {
                Directory.CreateDirectory(sysDir);
                if (!File.Exists(newPath) && File.Exists(oldPath))
                {
                    File.Copy(oldPath, newPath, false);
                    _configMigrated = true;
                }
            }
            catch
            {
                // не смогли создать system\ или скопировать — работаем со старым
                return File.Exists(oldPath) ? oldPath : newPath;
            }
            return newPath;
        }

        public WatchContext()
        {
            string cfg = ResolveConfigPath();
            _settings = new Settings(cfg);
            _settings.EnsureExists();
            _settings.Load();
            ApplyLogSettings();
            if (_configMigrated)
                Log.Write("CONFIG", "старый config.ini перенесён: копия в system\\config.ini, " +
                                    "старый файл оставлен на месте (можно удалить)");
            for (int i = 0; i < _settings.BadRules.Count; i++)
                Log.Write("CONFIG", "не удалось разобрать правило: " + _settings.BadRules[i]);
            for (int i = 0; i < _settings.RuleWarnings.Count; i++)
                Log.Write("CONFIG", "внимание: " + _settings.RuleWarnings[i]);

            _timer = new Timer();
            _timer.Interval = Clamp(_settings.PollIntervalMs, 250, 60000);
            _timer.Tick += delegate { Scan(); };

            InstallHook();
            ApplyScanPolicy();
            EnsureAutostartPath();   // путь автозапуска мог остаться от старой папки

            _tray = new NotifyIcon();
            _tray.Icon = AppIcon;
            _tray.Text = "Oktell Watch";
            _tray.ContextMenuStrip = BuildMenu();
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowInspector(); };

            _timer.Start();
            UpdateStatus();

            string mode = (_hook != IntPtr.Zero)
                ? "события показа/закрытия окон (мгновенно) + страховочный опрос"
                : "только опрос";
            Log.Write("START", "запущен; правил: " + _settings.Rules.Count +
                               "; обнаружение: " + mode +
                               "; интервал опроса " + _timer.Interval + " мс; конфиг: " + _settings.ConfigPath);
            _tray.BalloonTipTitle = "Oktell Watch";
            _tray.BalloonTipText = "Сторож запущен. Правил наблюдения: " + _settings.Rules.Count +
                                   ".\nПравый клик по значку — меню, двойной клик — инспектор.";
            try { _tray.ShowBalloonTip(4000); } catch { }
        }

        private static int Clamp(int v, int min, int max) { return v < min ? min : (v > max ? max : v); }

        // ------------------------------------------- мгновенное обнаружение (v1.1)
        private void InstallHook()
        {
            if (!_settings.UseHooks || _hook != IntPtr.Zero) return;
            try
            {
                _hookProc = OnWinEvent;
                // Диапазон 0x8001..0x8002: и EVENT_OBJECT_SHOW (новое окно),
                // и EVENT_OBJECT_DESTROY (наблюдаемое окно закрыли).
                _hook = WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_DESTROY, WinApi.EVENT_OBJECT_SHOW,
                    IntPtr.Zero, _hookProc, 0, 0, WinApi.WINEVENT_OUTOFCONTEXT);
            }
            catch { _hook = IntPtr.Zero; }
            Log.Write("CONFIG", _hook != IntPtr.Zero
                ? "хук событий окон установлен: показ и закрытие окон отслеживаются мгновенно"
                : "хук событий недоступен — работает только опрос");
        }

        private void UninstallHook()
        {
            if (_hook == IntPtr.Zero) return;
            try { WinApi.UnhookWinEvent(_hook); } catch { }
            _hook = IntPtr.Zero;
        }

        private void OnWinEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0) return; // интересуют окна (OBJID_WINDOW)

            // config.ini могли править, пока программа ждала событий: штамп
            // файла проверяем на каждом событии окон (не дожидаясь
            // страховочного опроса), но не чаще раза в hookCooldownMs —
            // тогда «перечитает в течение секунды» верно и при простое.
            int now = Environment.TickCount;
            if (_lastCfgTick == int.MinValue ||
                unchecked(now - _lastCfgTick) >= _hookCooldownMs)
            {
                _lastCfgTick = now;
                try { CheckIniChanged(); } catch { }
            }

            if (_paused || _captureHold) return;

            // Наблюдаемое окно уничтожено — закрываем трек и глушим звук
            // немедленно, без троттлинга и не дожидаясь ближайшего опроса:
            // звук не должен доигрывать, пока оператор уже говорит с клиентом.
            // Если уничтожено КОРНЕВОЕ окно, закрываются и треки его внутренних
            // элементов (с 1.2.3.5): правило scope:any держит внутренний элемент
            // формы Oktell, и раньше закрытие всей формы гасило звук только
            // по ближайшему страховочному опросу — до 15 секунд спустя.
            if (evt == WinApi.EVENT_OBJECT_DESTROY)
            {
                long key = hwnd.ToInt64();
                List<long> dead = null;
                foreach (KeyValuePair<long, Track> kv in _tracked)
                {
                    if (kv.Key == key || kv.Value.Info.RootHwnd == hwnd)
                    {
                        if (dead == null) dead = new List<long>();
                        dead.Add(kv.Key);
                    }
                }
                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++)
                    {
                        try { CloseTrack(dead[i], "окно закрыто"); }
                        catch (Exception ex) { Log.Write("ERROR", "CloseTrack(hook): " + ex.Message); }
                    }
                    return;
                }
            }

            // троттлинг скана. int.MinValue здесь — «скана ещё не было»:
            // раньше разность с ним переполнялась в минус, и все события
            // показа до первого страховочного опроса (15 с) проглатывались.
            now = Environment.TickCount;
            if (_lastScanTick != int.MinValue &&
                unchecked(now - _lastScanTick) < _hookCooldownMs)
            {
                RequestScan();   // событие не теряется: скан пройдёт после кулдауна
                return;
            }
            _lastScanTick = now;
            try { Scan(); }
            catch (Exception ex) { Log.Write("ERROR", "Scan(hook): " + ex.Message); }
        }

        // «Хвост» троттлинга (с 1.2.3.6). Показ формы почти всегда идёт
        // пачкой событий: окно, элементы, надписи. Первое событие запускает
        // скан, а события следующих hookCooldownMs мс раньше отбрасывались
        // безвозвратно — надпись, появившаяся в конце пачки, попадала в
        // скан только со страховочным опросом, до 15 секунд спустя. Теперь
        // каждое событие в кулдауне перезапускает короткий таймер: скан
        // проходит через hookCooldownMs после ПОСЛЕДНЕГО события серии.
        private void RequestScan()
        {
            if (_tailTimer == null)
            {
                _tailTimer = new Timer();
                _tailTimer.Tick += delegate
                {
                    _tailTimer.Stop();
                    if (_paused || _captureHold) return;
                    try { Scan(); }
                    catch (Exception ex) { Log.Write("ERROR", "Scan(tail): " + ex.Message); }
                };
            }
            _tailTimer.Interval = _hookCooldownMs;
            _tailTimer.Start();   // перезапуск: отсчёт от последнего события серии
        }

        // Полный опрос — страховка: при работающих хуках не чаще 15 секунд
        private void ApplyScanPolicy()
        {
            _hookCooldownMs = Clamp(_settings.HookCooldownMs, 50, 5000);
            UpdateTimerInterval();
            Collector.SetCacheTtl(_settings.ProcCacheSec);
        }

        // Интервал таймера опроса (с 1.2.3.6). Пока есть наблюдаемые окна
        // и включён повтор сигнала, таймер тикает в такт repeatEverySec:
        // раньше период повтора был привязан к опросу, и при работающих
        // хуках (опрос раз в 15 секунд) «повтор каждые 5 сек» соблюдался
        // только по случаю — когда оператор отошёл и событий нет, сигнал
        // повторялся втрое реже настроенного, а при активной мыши — чаще.
        private void UpdateTimerInterval()
        {
            int poll = Clamp(_settings.PollIntervalMs, 250, 60000);
            if (_settings.UseHooks && _hook != IntPtr.Zero && poll < 15000) poll = 15000;
            if (_tracked.Count > 0 && _settings.RepeatAlert)
            {
                int rep = Clamp(_settings.RepeatEverySec, 1, 600) * 1000;
                if (rep < poll) poll = rep;   // есть цель — тикаем в такт повтору
            }
            if (_timer.Interval != poll) _timer.Interval = poll;
        }

        private void ApplyLogSettings()
        {
            Log.Enabled = _settings.LogEnabled;
            Log.Folder = _settings.LogFolderAbsolute;
        }

        // ------------------------------------------------------------- меню
        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            _miStatus = new ToolStripMenuItem("Наблюдение: активно (целей: 0)");
            _miStatus.Enabled = false;

            _miPause = new ToolStripMenuItem("Пауза наблюдения");
            _miPause.Click += delegate { TogglePause(); };

            _miFront = new ToolStripMenuItem("Поднимать окно наверх");
            _miFront.Checked = _settings.BringToFront;
            _miFront.Click += delegate
            {
                _settings.BringToFront = !_settings.BringToFront;
                _miFront.Checked = _settings.BringToFront;
                try
                {
                    _settings.Ini.SetValue("alert", "bringToFront", _settings.BringToFront ? "true" : "false");
                    RefreshStamp();
                }
                catch { }
                Log.Write("CONFIG", "bringToFront=" + _settings.BringToFront);
            };

            _miAuto = new ToolStripMenuItem("Автозапуск с Windows");
            _miAuto.Checked = GetAutostart();
            _miAuto.Click += delegate
            {
                bool on = !_miAuto.Checked;
                try
                {
                    SetAutostart(on);
                    _miAuto.Checked = GetAutostart();
                    Log.Write("CONFIG", "автозапуск=" + _miAuto.Checked);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось изменить автозапуск: " + ex.Message,
                        "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            ToolStripMenuItem miInspector = new ToolStripMenuItem("Инспектор окон...");
            miInspector.Font = new Font(menu.Font, FontStyle.Bold);
            miInspector.Click += delegate { ShowInspector(); };

            ToolStripMenuItem miRules = new ToolStripMenuItem("Диспетчер правил...");
            miRules.Font = new Font(menu.Font, FontStyle.Bold);
            miRules.Click += delegate { ShowRulesManager(); };

            ToolStripMenuItem miLogs = new ToolStripMenuItem("Открыть папку с логами");
            miLogs.Click += delegate { OpenLogs(); };

            ToolStripMenuItem miHelp = new ToolStripMenuItem("Справка / Инструкция");
            miHelp.Click += delegate { HelpForm.ShowHelp(); };

            ToolStripMenuItem miExit = new ToolStripMenuItem("Выход");
            miExit.Click += delegate { ExitApp(); };

            menu.Items.Add(_miStatus);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miPause);
            menu.Items.Add(_miFront);
            menu.Items.Add(_miAuto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miInspector);
            menu.Items.Add(miRules);
            menu.Items.Add(miLogs);
            menu.Items.Add(miHelp);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            return menu;
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _miPause.Text = _paused ? "Продолжить наблюдение" : "Пауза наблюдения";
            if (_paused) StopSound("пауза наблюдения");
            Log.Write(_paused ? "PAUSE" : "RESUME",
                _paused ? "наблюдение приостановлено оператором" : "наблюдение возобновлено");
            UpdateStatus();
            UpdateTimerInterval();
        }

        private void OpenLogs()
        {
            try
            {
                string dir = Log.Folder;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть папку: " + ex.Message,
                    "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExitApp()
        {
            Log.Write("STOP", "завершение работы");
            try { StopSound("выход"); } catch { }
            UninstallHook();
            try { _timer.Stop(); } catch { }
            if (_tailTimer != null) { try { _tailTimer.Stop(); _tailTimer.Dispose(); } catch { } }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            Application.Exit();
        }

        // --------------------------------------------------- автозапуск
        private static bool GetAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return k != null && k.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        private static void SetAutostart(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (k == null) throw new Exception("нет доступа к реестру");
                if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValueName, false);
            }
        }

        // Программу могли перенести в другую папку: в реестре остался путь
        // к старой, галочка «Автозапуск» при этом стоит, но автозапуск
        // битый (с 1.2.3.6). При старте сравниваем и молча обновляем.
        private void EnsureAutostartPath()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null || k.GetValue(RunValueName) == null) return;
                    string cur = Convert.ToString(k.GetValue(RunValueName));
                    string want = "\"" + Application.ExecutablePath + "\"";
                    if (String.Equals(cur, want, StringComparison.OrdinalIgnoreCase)) return;
                    k.SetValue(RunValueName, want);
                    Log.Write("CONFIG", "автозапуск: в реестре был путь к старой папке — обновлён на " + want);
                }
            }
            catch { }
        }

        // --------------------------------------------------- сканирование
        private void Scan()
        {
            _lastScanTick = Environment.TickCount;
            try { CheckIniChanged(); } catch { }
            if (_paused || _captureHold) return;

            Dictionary<long, WindowInfo> found = new Dictionary<long, WindowInfo>();
            Dictionary<long, int> ruleOf = new Dictionary<long, int>();
            try { Collector.Collect(_settings, found, ruleOf); }
            catch (Exception ex)
            {
                Log.Write("ERROR", "Collect: " + ex.Message);
                return;
            }

            // сколько окон поймало каждое правило — для диспетчера правил
            _matchCounts = new int[_settings.Rules.Count];
            foreach (KeyValuePair<long, int> kv in ruleOf)
                if (kv.Value >= 0 && kv.Value < _matchCounts.Length) _matchCounts[kv.Value]++;

            // новые цели
            foreach (KeyValuePair<long, WindowInfo> kv in found)
            {
                if (_tracked.ContainsKey(kv.Key)) continue;
                Track t = new Track();
                t.Info = kv.Value;
                t.FirstSeen = DateTime.Now;
                t.LastAlert = DateTime.MinValue;
                int rn;
                t.RuleNo = ruleOf.TryGetValue(kv.Key, out rn) ? rn + 1 : 0;
                _tracked[kv.Key] = t;
                Log.Write("DETECTED", (kv.Value.IsChild ? "[внутр. окно] " : "") + kv.Value.Describe());
            }

            // исчезнувшие = обработанные: убираем ДО оповещения (с 1.2.3.6),
            // чтобы окно, закрытое между сканами, не успело получить лишний
            // сигнал и запись RE-ALERT в журнал
            List<long> gone = new List<long>();
            foreach (KeyValuePair<long, Track> kv in _tracked)
                if (!found.ContainsKey(kv.Key)) gone.Add(kv.Key);
            for (int i = 0; i < gone.Count; i++)
                CloseTrack(gone[i], "окно исчезло (обработано)");

            // оповещение: новые и повторные
            DateTime now = DateTime.Now;
            foreach (KeyValuePair<long, Track> kv in _tracked)
            {
                Track t = kv.Value;
                if (!t.Alerted)
                {
                    Alert(t);
                }
                else if (_settings.RepeatAlert &&
                         (now - t.LastAlert).TotalSeconds >= Math.Max(1, _settings.RepeatEverySec))
                {
                    Alert(t);
                }
            }

            UpdateStatus();
            UpdateTimerInterval();   // цели появились/исчезли — интервал в такт повтору
        }

        // Закрытие трека. Если наблюдаемых окон не осталось — звук гаснет
        // немедленно: оператор мог уже подтвердить задачу и говорить
        // с клиентом, доигрывающий сигнал в эфир попадать не должен.
        private void CloseTrack(long hwnd, string reason)
        {
            Track t;
            if (!_tracked.TryGetValue(hwnd, out t)) return;
            _tracked.Remove(hwnd);
            Log.Write("CLOSED", reason + "; сигналов: " + t.Alerts +
                                "; держалось: " + (int)(DateTime.Now - t.FirstSeen).TotalSeconds + " сек; " +
                                t.Info.Describe());
            if (_tracked.Count == 0) StopSound("наблюдаемых окон не осталось");
            UpdateStatus();
            UpdateTimerInterval();   // целей не осталось — возвращаем обычный интервал опроса
        }

        // Немедленная остановка звука: останавливаем наш плеер и (на случай
        // системного сигнала) всё, что играет на этом потоке через winmm.
        private void StopSound(string reason)
        {
            try
            {
                if (_player != null) { try { _player.Stop(); } catch { } }
                try { WinApi.PlaySound(null, IntPtr.Zero, WinApi.SND_ASYNC | WinApi.SND_PURGE); }
                catch { }
                Log.Write("SOUND", "звук остановлен (" + reason + ")");
            }
            catch { }
        }

        private void Alert(Track t)
        {
            t.LastAlert = DateTime.Now;
            t.Alerts++;
            t.Alerted = true;
            bool first = t.Alerts == 1;

            IntPtr target = t.Info.Hwnd;
            if (t.Info.IsChild && t.Info.RootHwnd != IntPtr.Zero) target = t.Info.RootHwnd;

            if (_settings.BringToFront && first) WinApi.ForceForeground(target);
            if (_settings.FlashTaskbar) WinApi.Flash(target);
            PlaySound();

            Log.Write(first ? "ALERT" : "RE-ALERT",
                (first ? "новая задача обратного звонка!" : "повтор сигнала №" + t.Alerts) +
                " " + t.Info.Describe());
        }

        private void PlaySound()
        {
            try
            {
                string f = _settings.SoundFile;
                if (!String.IsNullOrEmpty(f) && File.Exists(f))
                {
                    if (_player == null || !String.Equals(_playerFile, f, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_player != null) { try { _player.Dispose(); } catch { } }
                        _player = new SoundPlayer(f);
                        _playerFile = f;
                    }
                    try { _player.Play(); }
                    catch { SystemSounds.Exclamation.Play(); }
                }
                else
                {
                    SystemSounds.Exclamation.Play();
                }
            }
            catch { }
        }

        private void UpdateStatus()
        {
            try
            {
                _miStatus.Text = "Наблюдение: " + (_paused ? "ПАУЗА" : "активно") +
                                 " (целей: " + _tracked.Count + ")";
                string tt = "Oktell Watch — " + (_paused ? "ПАУЗА" : "наблюдение активно") +
                            ", целей: " + _tracked.Count;
                _tray.Text = tt.Length > 63 ? tt.Substring(0, 63) : tt;
            }
            catch { }
        }

        // --------------------------------------------------- горячая смена конфига
        private void RefreshStamp()
        {
            try { _settings.Stamp = File.GetLastWriteTimeUtc(_settings.ConfigPath); } catch { }
        }

        private void CheckIniChanged()
        {
            DateTime st;
            try { st = File.GetLastWriteTimeUtc(_settings.ConfigPath); } catch { return; }
            if (st == _settings.Stamp) return;

            _settings.Ini.Reload();
            _settings.ReadAll();
            ApplyLogSettings();
            if (_settings.UseHooks && _hook == IntPtr.Zero) InstallHook();
            if (!_settings.UseHooks && _hook != IntPtr.Zero) UninstallHook();
            ApplyScanPolicy();
            _miFront.Checked = _settings.BringToFront;
            RefreshStamp();
            Log.Write("CONFIG", "config.ini изменён и перечитан; правил: " + _settings.Rules.Count);
        }

        // --------------------------------------------------- инспектор и правила
        public void SetCaptureHold(bool on) { _captureHold = on; }

        public void ShowInspector()
        {
            try
            {
                if (_inspector == null || _inspector.IsDisposed)
                {
                    _inspector = new InspectorForm(this);
                    _inspector.Show();
                }
                else
                {
                    _inspector.Show();
                    _inspector.WindowState = FormWindowState.Normal;
                    _inspector.Activate();
                }
            }
            catch { }
        }

        public void ShowRulesManager()
        {
            try
            {
                if (_manager == null || _manager.IsDisposed)
                {
                    _manager = new RulesManagerForm(this);
                    _manager.Show();
                }
                else
                {
                    _manager.Show();
                    _manager.WindowState = FormWindowState.Normal;
                    _manager.Activate();
                }
            }
            catch { }
        }

        // --------------------------------------------------- данные для диспетчера
        public Settings Cfg { get { return _settings; } }

        public int[] MatchCounts { get { return _matchCounts; } }

        public List<WatchRule> CurrentRules()
        {
            return new List<WatchRule>(_settings.Rules);
        }

        public List<TrackView> SnapshotTracks()
        {
            List<TrackView> res = new List<TrackView>();
            foreach (KeyValuePair<long, Track> kv in _tracked)
            {
                Track t = kv.Value;
                TrackView v = new TrackView();
                v.Hwnd = kv.Key;
                v.Proc = t.Info.ProcessName;
                v.Title = t.Info.Title;
                v.IsChild = t.Info.IsChild;
                v.RuleNo = t.RuleNo;
                v.Alerts = t.Alerts;
                v.FirstSeen = t.FirstSeen;
                res.Add(v);
            }
            return res;
        }

        // Перезаписать правила в config.ini по списку из диспетчера
        public bool SaveRules(List<WatchRule> rules)
        {
            try
            {
                List<KeyValuePair<string, string>> items = _settings.Ini.SectionItems("watch");
                List<string> oldKeys = new List<string>();
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Key.ToLowerInvariant().StartsWith("rule")) oldKeys.Add(items[i].Key);
                for (int i = 0; i < oldKeys.Count; i++) _settings.Ini.DeleteKey("watch", oldKeys[i]);

                int n = 0;
                for (int i = 0; i < rules.Count; i++)
                {
                    WatchRule r = rules[i];
                    if (r == null) continue;
                    string raw = r.ToRaw();
                    if (String.IsNullOrEmpty(raw)) continue;
                    n++;
                    _settings.Ini.SetValue("watch", "rule" + n, raw);
                }
                // С 1.2.3.7: строки, которые не удалось разобрать при загрузке,
                // больше не стираются при сохранении — пишутся обратно как есть
                // (пересобрать их из полей нельзя: значения программе неизвестны)
                int savedBad = 0;
                for (int i = 0; i < _settings.BadRules.Count; i++)
                {
                    string line = _settings.BadRules[i];
                    int eq = line.IndexOf('=');
                    string val = (eq >= 0 ? line.Substring(eq + 1) : line).Trim();
                    if (val.Length == 0) continue;
                    n++;
                    _settings.Ini.SetValue("watch", "rule" + n, val);
                    savedBad++;
                }
                _settings.Ini.Reload();
                _settings.ReadAll();
                ApplyLogSettings();
                if (_settings.UseHooks && _hook == IntPtr.Zero) InstallHook();
                if (!_settings.UseHooks && _hook != IntPtr.Zero) UninstallHook();
                ApplyScanPolicy();
                RefreshStamp();
                Log.Write("CONFIG", "правила сохранены через диспетчер; всего: " + n +
                    (savedBad > 0
                        ? "; не разобранных при загрузке сохранено как есть: " + savedBad
                        : ""));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось сохранить правила: " + ex.Message,
                    "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        public void AddRule(string ruleText)
        {
            WatchRule r = WatchRule.Parse(ruleText);
            if (r == null)
            {
                MessageBox.Show("Не удалось разобрать правило.\nНужен хотя бы один параметр: proc:, title: или class:",
                    "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                int n = NextRuleNumber();
                string key = "rule" + n;
                _settings.Ini.SetValue("watch", key, r.Raw);
                _settings.Ini.Reload();
                _settings.ReadAll();
                RefreshStamp();
                Log.Write("CONFIG", "добавлено правило: " + key + "=" + r.Raw);
                MessageBox.Show("Правило добавлено:\n\n" + key + "=" + r.Raw +
                                "\n\nНаблюдение обновлено и продолжается.\n" +
                                "Управлять правилами — «Диспетчер правил...» в меню значка.",
                    "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось сохранить правило: " + ex.Message,
                    "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private int NextRuleNumber()
        {
            int max = 0;
            List<KeyValuePair<string, string>> items = _settings.Ini.SectionItems("watch");
            for (int i = 0; i < items.Count; i++)
            {
                string k = items[i].Key.ToLowerInvariant();
                if (!k.StartsWith("rule")) continue;
                int v;
                if (int.TryParse(k.Substring(4), out v) && v > max) max = v;
            }
            return max + 1;
        }

        Icon _appIcon;

        // Единый значок приложения (трей и заголовки окон) — рисуется один раз
        internal Icon AppIcon
        {
            get { if (_appIcon == null) _appIcon = BuildIcon(); return _appIcon; }
        }

        // --------------------------------------------------- значок
        // Логотип: тёмно-синий скруглённый квадрат, оранжевая галочка,
        // белый динамик с дугами звука (динамик — выше и левее, отдельно
        // от галки). Сначала берём настоящий значок, вшитый в EXE при
        // сборке (build.bat: /win32icon:OktellWatch.ico), — тогда трей и
        // заголовки окон всегда совпадают со значком файла. Если значок
        // не вшит (сборка без /win32icon) — рисуем запасной вариант сами.
        private static Icon BuildIcon()
        {
            try
            {
                Icon emb = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (emb != null) return emb;
            }
            catch { }
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                        // фон — тёмно-синий скруглённый квадрат
                        using (System.Drawing.Drawing2D.GraphicsPath bg = new System.Drawing.Drawing2D.GraphicsPath())
                        {
                            bg.AddArc(0, 0, 12, 12, 180, 90);
                            bg.AddArc(20, 0, 12, 12, 270, 90);
                            bg.AddArc(20, 20, 12, 12, 0, 90);
                            bg.AddArc(0, 20, 12, 12, 90, 90);
                            bg.CloseFigure();
                            using (SolidBrush b = new SolidBrush(Color.FromArgb(38, 62, 98)))
                                g.FillPath(b, bg);
                        }

                        // галочка — оранжевая, скруглённые концы
                        using (Pen p = new Pen(Color.FromArgb(245, 130, 32), 6.2f))
                        {
                            p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                            p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                            p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                            g.DrawLines(p, new[] {
                                new PointF(5.5f, 16.5f), new PointF(12.5f, 23.5f),
                                new PointF(26.5f, 7.5f) });
                        }

                        // динамик — белый, выше и левее галки (не касается
                        // её), справа две дуги звука; пропорции как у значка
                        using (SolidBrush w = new SolidBrush(Color.White))
                        {
                            g.FillRectangle(w, 5.4f, 6.1f, 2.9f, 4.3f);
                            g.FillPolygon(w, new[] {
                                new PointF(8.1f, 4.6f), new PointF(8.1f, 11.9f),
                                new PointF(12.1f, 14.6f), new PointF(12.1f, 1.9f) });
                            using (Pen p = new Pen(Color.White, 1.6f))
                            {
                                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                                g.DrawArc(p, 8.1f, 3.75f, 9.0f, 9.0f, -52f, 104f);
                                g.DrawArc(p, 6.6f, 2.25f, 12.0f, 12.0f, -52f, 104f);
                            }
                        }
                    }
                    IntPtr hIcon = bmp.GetHicon();
                    return Icon.FromHandle(hIcon);
                }
            }
            catch { return SystemIcons.Application; }
        }
    }

    // ------------------------------------------------------------------ Program
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex m = new Mutex(true, "OktellWatch_SingleInstance_7f3a", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Oktell Watch уже запущен (значок возле часов).",
                        "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                GC.KeepAlive(m);

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("ERROR", "UI: " + e.Exception.Message + " | " + e.Exception.StackTrace);
                    try
                    {
                        MessageBox.Show("Ошибка: " + e.Exception.Message,
                            "Oktell Watch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    catch { }
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Write("ERROR", "FATAL: " + e.ExceptionObject);
                };

                WinApi.EnableDpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WatchContext());
            }
        }
    }
}
