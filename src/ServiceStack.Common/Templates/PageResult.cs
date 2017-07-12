using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ServiceStack.Text;
using ServiceStack.Web;

#if NETSTANDARD1_3
using Microsoft.Extensions.Primitives;
#endif

namespace ServiceStack.Templates
{
    public interface IPageResult {}

    // Render a Template Page to the Response OutputStream
    public class PageResult : IPageResult, IStreamWriterAsync, IHasOptions
    {
        public TemplatePage Page { get; set; }

        public TemplatePage LayoutPage { get; set; }

        public object Model { get; set; }

        /// <summary>
        /// Add additional Args available to all pages 
        /// </summary>
        public Dictionary<string, object> Args { get; set; }

        /// <summary>
        /// Add additional template filters available to all pages
        /// </summary>
        public List<TemplateFilter> TemplateFilters { get; set; }

        public IDictionary<string, string> Options { get; set; }

        /// <summary>
        /// Specify the Content-Type of the Response 
        /// </summary>
        public string ContentType
        {
            get => Options.TryGetValue(HttpHeaders.ContentType, out string contentType) ? contentType : null;
            set => Options[HttpHeaders.ContentType] = value;
        }

        /// <summary>
        /// Transform the Page output using a chain of filters
        /// </summary>
        public List<Func<Stream, Task<Stream>>> PageFilters { get; set; }

        /// <summary>
        /// Transform the entire output using a chain of filters
        /// </summary>
        public List<Func<Stream, Task<Stream>>> OutputFilters { get; set; }

        public PageResult(TemplatePage page)
        {
            Page = page ?? throw new ArgumentNullException(nameof(page));
            Args = new Dictionary<string, object>();
            TemplateFilters = new List<TemplateFilter>();
            PageFilters = new List<Func<Stream, Task<Stream>>>();
            OutputFilters = new List<Func<Stream, Task<Stream>>>();
            Options = new Dictionary<string, string>
            {
                {HttpHeaders.ContentType, Page.Format.ContentType},
            };
        }

        public async Task WriteToAsync(Stream responseStream, CancellationToken token = default(CancellationToken))
        {
            if (OutputFilters.Count == 0)
            {
                await WriteToAsyncInternal(responseStream, token);
                return;
            }

            using (var ms = MemoryStreamFactory.GetStream())
            {
                await WriteToAsyncInternal(ms, token);
                Stream stream = ms;

                foreach (var filter in OutputFilters)
                {
                    stream.Position = 0;
                    stream = await filter(stream);
                }

                using (stream)
                {
                    stream.Position = 0;
                    await stream.CopyToAsync(responseStream);
                }
            }
        }

        internal async Task WriteToAsyncInternal(Stream responseStream, CancellationToken token)
        {
            await Init();

            if (LayoutPage != null)
            {
                await Task.WhenAll(LayoutPage.Init(), Page.Init());
            }
            else
            {
                await Page.Init();
                if (Page.LayoutPage != null)
                {
                    LayoutPage = Page.LayoutPage;
                    await LayoutPage.Init();
                }
            }

            token.ThrowIfCancellationRequested();

            if (LayoutPage != null)
            {
                foreach (var fragment in LayoutPage.PageFragments)
                {
                    if (fragment is PageStringFragment str)
                    {
                        await responseStream.WriteAsync(str.ValueBytes, token);
                    }
                    else if (fragment is PageVariableFragment var)
                    {
                        if (var.Binding.Equals(TemplateConstants.Page))
                        {
                            await WritePageAsync(Page, responseStream, null, token);
                        }
                        else if (IsPageOrPartial(var))
                        {
                            var value = GetValue(var);
                            var page = await Page.Context.GetPage(value.ToString()).Init();
                            await WritePageAsync(page, responseStream, GetPageParams(var), token);
                        }
                        else
                        {
                            await responseStream.WriteAsync(EvaluateVarAsBytes(var, null), token);
                        }
                    }
                }
            }
            else
            {
                await WritePageAsync(Page, responseStream, null, token);
            }
        }

        private bool hasInit;
        public async Task<PageResult> Init()
        {
            if (hasInit)
                return this;

            if (Model != null)
            {
                var explodeModel = Model.ToObjectDictionary();
                foreach (var entry in explodeModel)
                {
                    Args[entry.Key] = entry.Value ?? JsNull.Value;
                }
            }
            Args[TemplateConstants.Model] = Model ?? JsNull.Value;

            foreach (var filter in TemplateFilters)
            {
                Page.Context.InitFilter(filter);
            }

            await Page.Init();

            hasInit = true;

            return this;
        }

        private void AssertInit()
        {
            if (!hasInit)
                throw new NotSupportedException("PageResult.Init() required for this operation.");
        }

        public async Task WritePageAsync(TemplatePage page, Stream responseStream, Dictionary<string, object> scopedParams = null,
            CancellationToken token = default(CancellationToken))
        {
            if (PageFilters.Count == 0)
            {
                await WritePageAsyncInternal(page, responseStream, scopedParams, token);
                return;
            }

            using (var ms = MemoryStreamFactory.GetStream())
            {
                await WritePageAsyncInternal(page, ms, scopedParams, token);
                Stream stream = ms;

                foreach (var filter in PageFilters)
                {
                    stream.Position = 0;
                    stream = await filter(stream);
                }

                using (stream)
                {
                    stream.Position = 0;
                    await stream.CopyToAsync(responseStream);
                }
            }
        }

        internal async Task WritePageAsyncInternal(TemplatePage page, Stream responseStream, Dictionary<string, object> scopedParams,
            CancellationToken token = default(CancellationToken))
        {
            foreach (var fragment in page.PageFragments)
            {
                if (fragment is PageStringFragment str)
                {
                    await responseStream.WriteAsync(str.ValueBytes, token);
                }
                else if (fragment is PageVariableFragment var)
                {
                    if (IsPageOrPartial(var))
                    {
                        var value = GetValue(var);
                        var subPage = await Page.Context.GetPage(value.ToString()).Init();
                        await WritePageAsync(subPage, responseStream, CombineParams(scopedParams, GetPageParams(var)), token);
                    }
                    else
                    {
                        await responseStream.WriteAsync(EvaluateVarAsBytes(var, scopedParams), token);
                    }
                }
            }
        }

        private static bool IsPageOrPartial(PageVariableFragment var)
        {
            var name = var.FilterExpressions.FirstOrDefault()?.Name ?? var.Expression?.Name;
            var isPartial = name.HasValue && (name.Value.Equals(TemplateConstants.Page) || name.Value.Equals(TemplateConstants.Partial));
            return isPartial;
        }

        private static Dictionary<string, object> CombineParams(Dictionary<string, object> parentParams, Dictionary<string, object> scopedParams)
        {
            if (parentParams == null)
                return scopedParams;
            if (scopedParams == null)
                return parentParams;
            
            var to = new Dictionary<string, object>();
            foreach (var entry in parentParams)
            {
                to[entry.Key] = entry.Value;
            }
            foreach (var entry in scopedParams)
            {
                to[entry.Key] = entry.Value;
            }
            return to;
        }

        private static Dictionary<string, object> GetPageParams(PageVariableFragment var)
        {
            Dictionary<string, object> scopedParams = null;
            if (var.FilterExpressions.Length > 0)
            {
                if (var.FilterExpressions[0].Args.Count > 0)
                {
                    var.FilterExpressions[0].Args[0].ParseNextToken(out object argValue, out _);
                    scopedParams = argValue as Dictionary<string, object>;
                }
            }
            return scopedParams;
        }

        private byte[] EvaluateVarAsBytes(PageVariableFragment var, Dictionary<string, object> scopedParams)
        {
            var value = Evaluate(var, scopedParams);
            var bytes = value != null
                ? Page.Format.EncodeValue(value).ToUtf8Bytes()
                : var.OriginalTextBytes;
            return bytes;
        }

        private object GetValue(PageVariableFragment var)
        {
            var value = var.Value ??
                (var.Binding.HasValue ? GetValue(var.NameString, null) : null);
            
            return value;
        }

        private object Evaluate(PageVariableFragment var, Dictionary<string, object> scopedParams = null)
        {
            var value = var.Value ??
                (var.Binding.HasValue
                    ? GetValue(var.NameString, scopedParams)
                    : var.Expression != null
                        ? var.Expression.IsBinding()
                            ? EvaluateBinding(var.Expression.Name, scopedParams)
                            : Evaluate(var, var.Expression, scopedParams)
                        : null);

            if (value == null)
            {
                if (!var.Binding.HasValue) 
                    return null;
                
                var hasFilterAsBinding = GetFilterInvoker(var.Binding, 0, out TemplateFilter filter);
                if (hasFilterAsBinding != null)
                {
                    value = InvokeFilter(hasFilterAsBinding, filter, new object[0], var.Expression);
                }
                else
                {
                    var handlesUnknownValue = false;
                    if (var.FilterExpressions.Length > 0)
                    {
                        var filterName = var.FilterExpressions[0].Name;
                        var filterArgs = 1 + var.FilterExpressions[0].Args.Count;
                        handlesUnknownValue = TemplateFilters.Any(x => x.HandlesUnknownValue(filterName, filterArgs)) ||
                                              Page.Context.TemplateFilters.Any(x => x.HandlesUnknownValue(filterName, filterArgs));
                    }

                    if (!handlesUnknownValue)
                        return null;
                }
            }

            if (value == JsNull.Value)
                value = null;

            scopedParams = ReplaceAnyBindings(value) as Dictionary<string, object>;
            value = ReplaceAnyBindings(value, scopedParams);

            for (var i = 0; i < var.FilterExpressions.Length; i++)
            {
                var cmd = var.FilterExpressions[i];
                var invoker = GetFilterInvoker(cmd.Name, 1 + cmd.Args.Count, out TemplateFilter filter);
                if (invoker == null)
                {
                    if (i == 0)
                        return null; // ignore on server if first filter is missing  

                    throw new Exception($"Filter not found: {cmd} in '{Page.File.VirtualPath}'");
                }

                var args = new object[1 + cmd.Args.Count];
                args[0] = value;

                for (var cmdIndex = 0; cmdIndex < cmd.Args.Count; cmdIndex++)
                {
                    var arg = cmd.Args[cmdIndex];
                    var varValue = Evaluate(var, arg, scopedParams);
                    args[1 + cmdIndex] = varValue;
                }

                value = InvokeFilter(invoker, filter, args, cmd);
            }

            if (value == null)
                return string.Empty; // treat as empty value if evaluated to null

            return value;
        }

        private object ReplaceAnyBindings(object value, Dictionary<string, object> scopedParams = null)
        {
            if (value is JsBinding valueBinding)
            {
                return GetValue(valueBinding.Binding.Value, scopedParams);
            }
            if (value is Dictionary<string, object> map)
            {
                var keys = map.Keys.ToArray();
                foreach (var key in keys)
                {
                    if (map[key] is JsBinding binding)
                    {
                        map[key] = GetValue(binding.Binding.Value, scopedParams);
                    }
                }
            }
            if (value is List<object> list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is JsBinding binding)
                    {
                        list[i] = GetValue(binding.Binding.Value, scopedParams);
                    }
                }
            }
            return value;
        }

        private object InvokeFilter(MethodInvoker invoker, TemplateFilter filter, object[] args, JsExpression expr)
        {
            if (invoker == null)
                throw new NotSupportedException($"Filter {expr.Binding} does not exist");

            try
            {
                return invoker(filter, args);
            }
            catch (Exception ex)
            {
                var exResult = Page.Format.OnExpressionException(this, ex);
                if (exResult != null)
                    return exResult;
                
                throw new TargetInvocationException($"Failed to invoke filter {expr.Binding}", ex);
            }
        }

        private object Evaluate(PageVariableFragment var, StringSegment arg, Dictionary<string, object> scopedParams)
        {
            var.ParseLiteral(arg, out object outValue, out JsBinding binding);

            if (binding is JsExpression expr)
            {
                var value = Evaluate(var, expr, scopedParams);
                return value;
            }
            if (binding != null)
            {
                return GetValue(binding.Binding.Value, scopedParams);
            }
            return outValue;
        }

        private object Evaluate(PageVariableFragment var, JsExpression expr, Dictionary<string, object> scopedParams)
        {
            var invoker = GetFilterInvoker(expr.Name, expr.Args.Count, out TemplateFilter filter);

            var args = new object[expr.Args.Count];
            for (var i = 0; i < expr.Args.Count; i++)
            {
                var arg = expr.Args[i];
                var varValue = Evaluate(var, arg, scopedParams);
                args[i] = varValue;
            }

            var value = InvokeFilter(invoker, filter, args, expr);
            return value;
        }

        private MethodInvoker GetFilterInvoker(StringSegment name, int argsCount, out TemplateFilter filter)
        {
            foreach (var tplFilter in TemplateFilters)
            {
                var invoker = tplFilter.GetInvoker(name, argsCount);
                if (invoker != null)
                {
                    filter = tplFilter;
                    return invoker;
                }
            }

            foreach (var tplFilter in Page.Context.TemplateFilters)
            {
                var invoker = tplFilter.GetInvoker(name, argsCount);
                if (invoker != null)
                {
                    filter = tplFilter;
                    return invoker;
                }
            }

            filter = null;
            return null;
        }

        private object GetValue(string name, Dictionary<string, object> scopedParams)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            var value = scopedParams != null && scopedParams.TryGetValue(name, out object obj)
                ? obj
                : Args.TryGetValue(name, out obj)
                    ? obj
                    : Page.Args.TryGetValue(name, out obj)
                        ? obj
                        : (LayoutPage != null && LayoutPage.Args.TryGetValue(name, out obj))
                            ? obj
                            : Page.Context.Args.TryGetValue(name, out obj)
                                ? obj
                                : null;
            return value;
        }

        private static readonly char[] VarDelimiters = { '.', '[', ' ' };

        public object EvaluateBinding(string expr, Dictionary<string, object> scopedParams = null)
        {
            return EvaluateBinding(expr.ToStringSegment(), scopedParams);
        }

        public object EvaluateBinding(StringSegment expr, Dictionary<string, object> scopedParams)
        {
            if (expr.IsNullOrWhiteSpace())
                return null;
            
            AssertInit();
            
            expr = expr.Trim();
            var pos = expr.IndexOfAny(VarDelimiters, 0);
            if (pos == -1)
                return GetValue(expr.Value, scopedParams);
            
            var target = expr.Substring(0, pos);

            var targetValue = GetValue(target, scopedParams);
            if (targetValue == null)
                return null;

            if (targetValue == JsNull.Value)
                return JsNull.Value;

            var fn = Page.File.Directory.VirtualPath != TemplateConstants.TempFilePath
                ? Page.Context.GetExpressionBinder(targetValue.GetType(), expr)
                : TemplatePageUtils.Compile(targetValue.GetType(), expr);

            try
            {
                var value = fn(targetValue);
                return value;
            }
            catch (Exception ex)
            {
                var exResult = Page.Format.OnExpressionException(this, ex);
                if (exResult != null)
                    return exResult;
                
                throw new BindingExpressionException($"Could not evaluate expression '{expr}'", null, expr.Value, ex);
            }
        }

        private string result;
        public string Result
        {
            get
            {
                try
                {
                    if (result != null)
                        return result;
    
                    Init().Wait();
                    result = this.RenderToStringAsync().Result;
                    return result;
                }
                catch (AggregateException e)
                {
                    throw e.UnwrapIfSingleException();
                }
            }
        }
    }

    public class BindingExpressionException : Exception
    {
        public string Expression { get; }
        public string Member { get; }

        public BindingExpressionException(string message, string member, string expression, Exception inner=null)
            : base(message, inner)
        {
            Expression = expression;
            Member = member;
        }
    }
}