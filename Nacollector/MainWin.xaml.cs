﻿using CefSharp;
using Nacollector.Browser;
using Nacollector.JsActions;
using NacollectorUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Drawing;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using NacollectorUtils.Settings;
using System.IO;
using System.Security.Policy;

namespace Nacollector
{
    public partial class MainWin : Window
    {
        public static MainWin _mainWin;
        public static CrBrowser crBrowser;
        public static CrDownloads crDownloads;
        public static CrBrowserCookieGetter crCookieGetter;

        public MainWin()
        {
            _mainWin = this;

            InitializeComponent(); // 初始化控件
            InitBrowser(); // 初始化浏览器
        }

        private void InitBrowser()
        {
            // 初始化内置浏览器
#warning 记得修改
#if DEBUG
            string htmlPath = Utils.GetHtmlResPath("index.html");
            if (string.IsNullOrEmpty(htmlPath))
            {
                Application.Current.Shutdown(); // 退出程序
            }
#else
            string htmlPath = "http://127.0.0.1:8080";
#endif
            crBrowser = new CrBrowser(this, htmlPath);

            // Need Update: https://github.com/cefsharp/CefSharp/issues/2246

            //For legacy biding we'll still have support for
            CefSharpSettings.LegacyJavascriptBindingEnabled = true;
            crBrowser.GetBrowser().RegisterAsyncJsObject("AppAction", new AppAction(this, crBrowser));
            crBrowser.GetBrowser().RegisterAsyncJsObject("TaskController", new TaskControllerAction(this, crBrowser));

            //crBrowser.GetBrowser().FrameLoadEnd += new EventHandler<FrameLoadEndEventArgs>(SplashScreen_Browser_FrameLoadEnd); // 浏览器初始化完毕时执行

            crDownloads = new CrDownloads(crBrowser);

            browserContainer.Content = crBrowser.GetBrowser();

            //crCookieGetter = new CrBrowserCookieGetter();
        }

        public void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }

        public void ShowSystemMenu(System.Drawing.Point point)
        {


        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 是否退出弹窗
            string dialogTxt = "确定退出 Nacollector？";

            // 下载任务数统计
            int downloadingTaskNum = Convert.ToInt32(crBrowser.EvaluateScript("Downloads.countDownloadingTask();", 0, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            if (downloadingTaskNum > 0)
                dialogTxt = $"有 {downloadingTaskNum} 个下载任务仍在继续！确定结束下载并关闭程序？";

            System.Windows.Forms.DialogResult dr = System.Windows.Forms.MessageBox.Show(dialogTxt, "退出 Nacollector", System.Windows.Forms.MessageBoxButtons.OKCancel);
            if (dr == System.Windows.Forms.DialogResult.OK)
            {
                e.Cancel = false;
            }
            else
            {
                e.Cancel = true;
            }
        }

        ///
        /// 任务执行
        ///
        public CrBrowser GetCrBrowser()
        {
            return crBrowser;
        }

        public CrBrowserCookieGetter GetCrCookieGetter()
        {
            return crCookieGetter;
        }

        public AppDomain _spiderRawDomain = null;
        public SpiderDomain _spiderDomain = null;

        public SpiderDomain GetLoadSpiderDomain()
        {
            if (this._spiderDomain != null)
            {
                return this._spiderDomain;
            }

            // 创建新的 AppDomain
            AppDomainSetup domaininfo = new AppDomainSetup
            {
                ApplicationBase = Environment.CurrentDirectory
            };
            Evidence adevidence = AppDomain.CurrentDomain.Evidence;
            AppDomain rawDomain = AppDomain.CreateDomain("NacollectorSpiders", adevidence, domaininfo);

            // 动态加载 dll
            Type type = typeof(SpiderDomain);
            var spiderDomain = (SpiderDomain)rawDomain.CreateInstanceAndUnwrap(type.Assembly.FullName, type.FullName);

            spiderDomain.LoadAssembly(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NacollectorSpiders.dll"));

            this._spiderRawDomain = rawDomain;
            this._spiderDomain = spiderDomain;
            return spiderDomain;
        }

        public void UnloadSpiderDomain()
        {
            if (_spiderRawDomain == null)
            {
                return;
            }

            // 卸载 dll
            AppDomain.Unload(_spiderRawDomain);
            _spiderRawDomain = null;
            _spiderDomain = null;
        }

        /// <summary>
        /// 任务线程
        /// </summary>
        public Dictionary<string, Thread> taskThreads = new Dictionary<string, Thread>();

        public void AbortTask(string taskId)
        {
            if (!taskThreads.ContainsKey(taskId))
                return;

            // 主线程委托执行，防止 Abort() 后面的代码无效
            this.Dispatcher.BeginInvoke((Action)delegate
            {
                if (taskThreads[taskId].IsAlive)
                {
                    taskThreads[taskId].Abort();
                }

                taskThreads.Remove(taskId);
                if (taskThreads.Count <= 0)
                {
                    UnloadSpiderDomain();
                }
            });

            return;
        }

        public void NewTaskThread(SpiderSettings settings)
        {
            // 创建任务执行线程
            var thread = new Thread(new ParameterizedThreadStart(StartTask))
            {
                IsBackground = true
            };
            thread.Start(settings);

            // 加入 Threads Dictionary
            taskThreads.Add(settings.TaskId, thread);
        }

        private void ShowCreateTaskError(string taskId, string msg, Exception ex)
        {
            string errorText = "任务新建失败, " + msg;
            Logging.Error($"{errorText} [{ex.Data.Keys + ": " + ex.Message}]");
            System.Windows.Forms.MessageBox.Show(errorText, "Nacollector 错误", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            AbortTask(taskId);
        }

        /// <summary>
        /// 开始执行任务
        /// </summary>
        /// <param name="obj"></param>
        public void StartTask(object obj)
        {
            var settings = (SpiderSettings)obj;

            SpiderDomain spiderDomain = null;
            try
            {
                spiderDomain = GetLoadSpiderDomain();
            }
            catch (Exception ex)
            {
                ShowCreateTaskError((string)settings.TaskId, "NacollectorSpiders.dll 调用失败", ex);
#if DEBUG
                Debugger.Break();
#endif
                return;
            }

            // 调用目标函数
            try
            {
                // Funcs
                settings.BrowserJsRunFunc = new Action<string>((string str) => {
                    this.GetCrBrowser().RunJS(str);
                });
                settings.CrBrowserCookieGetter = new Func<CookieGetterSettings, string>((CookieGetterSettings cgSettings) =>
                {
                    GetCrCookieGetter().CreateNew(cgSettings.StartUrl, cgSettings.EndUrlReg, cgSettings.Caption);

                    if (cgSettings.InputAutoCompleteConfig != null)
                    {
                        GetCrCookieGetter().UseInputAutoComplete(
                            (string)cgSettings.InputAutoCompleteConfig["pageUrlReg"],
                            (List<string>)cgSettings.InputAutoCompleteConfig["inputElemCssSelectors"]
                        );
                    }

                    GetCrCookieGetter().BeginWork();

                    return GetCrCookieGetter().GetCookieStr();
                });

                spiderDomain.Invoke("NacollectorSpiders.PokerDealer", "NewTask", settings);
            }
            catch (ThreadAbortException)
            {
                // 进程正在被中止
                // 不进行操作
            }
            catch (Exception ex)
            {
                ShowCreateTaskError((string)settings.TaskId, "无法调用 NacollectorSpiders.PokerDealer.NewTask", ex);
#if DEBUG
                Debugger.Break();
#endif
                return;
            }

            AbortTask((string)settings.TaskId);
        }

        /// <summary>
        /// 执行爬虫任务，新的 AppDomain 中
        /// </summary>
        public class SpiderDomain : MarshalByRefObject
        {
            Assembly assembly = null;

            public void LoadAssembly(string assemblyPath)
            {
                try
                {
                    assembly = Assembly.LoadFile(assemblyPath);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(ex.Message);
                }
            }

            public bool Invoke(string fullClassName, string methodName, params object[] args)
            {
                if (assembly == null)
                    return false;
                Type tp = assembly.GetType(fullClassName);
                if (tp == null)
                    return false;
                MethodInfo method = tp.GetMethod(methodName);
                if (method == null)
                    return false;
                object obj = Activator.CreateInstance(tp);
                method.Invoke(obj, args);
                return true;
            }
        }
    }
}