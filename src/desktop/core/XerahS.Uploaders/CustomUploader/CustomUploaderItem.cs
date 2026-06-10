#region License Information (GPL v3)

/*
    XerahS - The Avalonia UI implementation of ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using XerahS.Common.Utilities;

using Newtonsoft.Json;
using XerahS.Common;
using System.Collections.Specialized;
using System.ComponentModel;

namespace XerahS.Uploaders
{
    public class CustomUploaderItem
    {
        [DefaultValue("")]
        public string Version { get; set; } = string.Empty;

        [DefaultValue("")]
        public string Name { get; set; } = string.Empty;

        public bool ShouldSerializeName() => !string.IsNullOrEmpty(Name) && Name != URLHelpers.GetHostName(RequestURL);

        [DefaultValue(CustomUploaderDestinationType.None)]
        public CustomUploaderDestinationType DestinationType { get; set; }

        [DefaultValue(HttpMethod.POST), JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public HttpMethod RequestMethod { get; set; } = HttpMethod.POST;

        [DefaultValue("")]
        public string RequestURL { get; set; } = string.Empty;

        [DefaultValue(null)]
        public Dictionary<string, string>? Parameters { get; set; }

        public bool ShouldSerializeParameters() => Parameters != null && Parameters.Count > 0;

        [DefaultValue(null)]
        public Dictionary<string, string>? Headers { get; set; }

        public bool ShouldSerializeHeaders() => Headers != null && Headers.Count > 0;

        [DefaultValue(CustomUploaderBody.None)]
        public CustomUploaderBody Body { get; set; }

        [DefaultValue(null)]
        public Dictionary<string, string>? Arguments { get; set; }

        public bool ShouldSerializeArguments() => (Body == CustomUploaderBody.MultipartFormData || Body == CustomUploaderBody.FormURLEncoded) &&
            Arguments != null && Arguments.Count > 0;

        [DefaultValue("")]
        public string FileFormName { get; set; } = string.Empty;

        public bool ShouldSerializeFileFormName() => Body == CustomUploaderBody.MultipartFormData && !string.IsNullOrEmpty(FileFormName);

        [DefaultValue("")]
        public string Data { get; set; } = string.Empty;

        public bool ShouldSerializeData() => (Body == CustomUploaderBody.JSON || Body == CustomUploaderBody.XML) && !string.IsNullOrEmpty(Data);

        [DefaultValue("")]
        public string URL { get; set; } = string.Empty;

        [DefaultValue("")]
        public string ThumbnailURL { get; set; } = string.Empty;

        [DefaultValue("")]
        public string DeletionURL { get; set; } = string.Empty;

        [DefaultValue("")]
        public string ErrorMessage { get; set; } = string.Empty;

        private CustomUploaderItem()
        {
        }

        public static CustomUploaderItem Init()
        {
            return new CustomUploaderItem()
            {
                Version = SystemInfo.GetApplicationVersion(),
                RequestMethod = HttpMethod.POST,
                Body = CustomUploaderBody.MultipartFormData
            };
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(Name))
            {
                return Name;
            }

            string name = URLHelpers.GetHostName(RequestURL);

            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            return "Name";
        }

        public string GetFileName()
        {
            return ToString() + ".sxcu";
        }

        public string GetRequestURL(CustomUploaderInput input)
        {
            if (string.IsNullOrEmpty(RequestURL))
            {
                throw new Exception(ShareX.UploadersLib.Properties.Resources.CustomUploaderItem_GetRequestURL_RequestURLMustBeConfigured);
            }

            ShareXCustomUploaderSyntaxParser parser = new ShareXCustomUploaderSyntaxParser(input);
            parser.URLEncode = true;
            string url = parser.Parse(RequestURL);

            url = URLHelpers.FixPrefix(url);

            Dictionary<string, string> parameters = GetParameters(input);
            return URLHelpers.CreateQueryString(url, parameters);
        }

        public Dictionary<string, string> GetParameters(CustomUploaderInput input)
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>();

            if (Parameters != null)
            {
                ShareXCustomUploaderSyntaxParser parser = new ShareXCustomUploaderSyntaxParser(input);
                parser.UseNameParser = true;

                foreach (KeyValuePair<string, string> parameter in Parameters)
                {
                    parameters.Add(parameter.Key, parser.Parse(parameter.Value));
                }
            }

            return parameters;
        }

        public string GetContentType()
        {
            switch (Body)
            {
                case CustomUploaderBody.MultipartFormData:
                    return RequestHelpers.ContentTypeMultipartFormData;
                case CustomUploaderBody.FormURLEncoded:
                    return RequestHelpers.ContentTypeURLEncoded;
                case CustomUploaderBody.JSON:
                    return RequestHelpers.ContentTypeJSON;
                case CustomUploaderBody.XML:
                    return RequestHelpers.ContentTypeXML;
                case CustomUploaderBody.Binary:
                    return RequestHelpers.ContentTypeOctetStream;
            }

            return RequestHelpers.ContentTypeOctetStream;
        }

        public string GetData(CustomUploaderInput input)
        {
            NameParser nameParser = new NameParser(NameParserType.Text);
            string result = nameParser.Parse(Data);

            Dictionary<string, string> replace = new Dictionary<string, string>();
            replace.Add("{input}", EncodeBodyData(input.Input));
            replace.Add("{filename}", EncodeBodyData(input.FileName));
            result = result.BatchReplace(replace, StringComparison.OrdinalIgnoreCase);

            return result;
        }

        private string EncodeBodyData(string input)
        {
            if (!string.IsNullOrEmpty(input))
            {
                if (Body == CustomUploaderBody.JSON)
                {
                    return URLHelpers.JSONEncode(input);
                }
                else if (Body == CustomUploaderBody.XML)
                {
                    return URLHelpers.XMLEncode(input);
                }
            }

            return input;
        }

        public string GetFileFormName()
        {
            if (string.IsNullOrEmpty(FileFormName))
            {
                throw new Exception(ShareX.UploadersLib.Properties.Resources.CustomUploaderItem_GetFileFormName_FileFormNameMustBeConfigured);
            }

            return FileFormName;
        }

        public Dictionary<string, string> GetArguments(CustomUploaderInput input)
        {
            Dictionary<string, string> arguments = new Dictionary<string, string>();

            if (Arguments != null)
            {
                ShareXCustomUploaderSyntaxParser parser = new ShareXCustomUploaderSyntaxParser(input);
                parser.UseNameParser = true;

                foreach (KeyValuePair<string, string> arg in Arguments)
                {
                    arguments.Add(arg.Key, parser.Parse(arg.Value));
                }
            }

            return arguments;
        }

        public NameValueCollection GetHeaders(CustomUploaderInput input)
        {
            if (Headers != null && Headers.Count > 0)
            {
                NameValueCollection collection = new NameValueCollection();

                ShareXCustomUploaderSyntaxParser parser = new ShareXCustomUploaderSyntaxParser(input);
                parser.UseNameParser = true;

                foreach (KeyValuePair<string, string> header in Headers)
                {
                    collection.Add(header.Key, parser.Parse(header.Value));
                }

                return collection;
            }

            return new NameValueCollection();
        }

        public void ParseResponse(UploadResult result, ResponseInfo responseInfo, UploaderErrorManager errors, CustomUploaderInput input, bool isShortenedURL = false)
        {
            if (result != null && responseInfo != null)
            {
                result.ResponseInfo = responseInfo;

                if (responseInfo.ResponseText == null)
                {
                    responseInfo.ResponseText = "";
                }

                ShareXCustomUploaderSyntaxParser parser = new ShareXCustomUploaderSyntaxParser()
                {
                    FileName = input.FileName,
                    ResponseInfo = responseInfo,
                    URLEncode = true
                };

                if (responseInfo.IsSuccess)
                {
                    string url;

                    if (!string.IsNullOrEmpty(URL))
                    {
                        url = parser.Parse(URL);

                        if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(URL) && URL.Contains("{output:"))
                        {
                            result.IsURLExpected = false;
                        }
                    }
                    else
                    {
                        url = parser.ResponseInfo.ResponseText;
                    }

                    if (isShortenedURL)
                    {
                        result.ShortenedURL = url;
                    }
                    else
                    {
                        result.URL = url;
                    }

                    result.ThumbnailURL = parser.Parse(ThumbnailURL);
                    result.DeletionURL = parser.Parse(DeletionURL);
                }
                else
                {
                    if (!string.IsNullOrEmpty(ErrorMessage))
                    {
                        string parsedErrorMessage = parser.Parse(ErrorMessage);

                        if (!string.IsNullOrEmpty(parsedErrorMessage))
                        {
                            errors.AddFirst(parsedErrorMessage);
                        }
                    }
                }
            }
        }

        public void TryParseResponse(UploadResult result, ResponseInfo? responseInfo, UploaderErrorManager errors, CustomUploaderInput input, bool isShortenedURL = false)
        {
            try
            {
                ParseResponse(result, responseInfo ?? new ResponseInfo(), errors, input, isShortenedURL);
            }
            catch (JsonReaderException e)
            {
                string hostName = URLHelpers.GetHostName(RequestURL);
                errors.AddFirst($"Invalid response content is returned from host ({hostName}), expected response content is JSON." +
                    Environment.NewLine + Environment.NewLine + e);
            }
            catch (Exception e)
            {
                string hostName = URLHelpers.GetHostName(RequestURL);
                errors.AddFirst($"Unable to parse response content returned from host ({hostName})." +
                    Environment.NewLine + Environment.NewLine + e);
            }
        }

        public void CheckBackwardCompatibility()
        {
            // Check if this is a XerahS custom uploader (versions 0.x.x)
            bool isXerahSVersion = !string.IsNullOrEmpty(Version) && Version.StartsWith("0.");

            if (isXerahSVersion)
            {
                CheckRequestURL();

                // A XerahS-stamped file can still hold legacy ShareX `$func:arg$` response syntax — e.g.
                // a service-exported .sxcu (ShareX 13.x format using $json:url$) that XerahS imported and
                // re-saved, keeping the legacy syntax while stamping a 0.x version. Migrate any field that
                // still uses the legacy syntax so the response parser resolves it instead of returning the
                // template verbatim. Per-field detection leaves already-modern `{...}` fields untouched.
                MigrateLegacyResponseSyntax();
                return;
            }

            // Legacy ShareX compatibility checks
            if (string.IsNullOrEmpty(Version) || SystemInfo.CompareVersion(Version, "12.3.1") <= 0)
            {
                throw new Exception("Unsupported custom uploader" + ": " + ToString());
            }

            CheckRequestURL();

            if (SystemInfo.CompareVersion(Version, "13.7.1") <= 0)
            {
                // Genuinely old ShareX (<= 13.7.1) files use the legacy $func:arg$ syntax; convert it
                // through the same targeted converter (only known function tokens, never literal '$').
                MigrateLegacyResponseSyntax();
                Version = SystemInfo.GetApplicationVersion();
            }
        }

        /// <summary>
        /// Migrates legacy ShareX <c>$func:arg$</c> response syntax to the modern <c>{func:arg}</c> form
        /// across every field, skipping fields that are already modern. Used for files that carry a
        /// modern/XerahS version stamp but still contain legacy syntax.
        /// </summary>
        public void MigrateLegacyResponseSyntax()
        {
            RequestURL = MigrateFieldIfLegacy(RequestURL);

            if (Parameters != null)
            {
                foreach (string key in Parameters.Keys.ToList())
                {
                    Parameters[key] = MigrateFieldIfLegacy(Parameters[key]);
                }
            }

            if (Headers != null)
            {
                foreach (string key in Headers.Keys.ToList())
                {
                    Headers[key] = MigrateFieldIfLegacy(Headers[key]);
                }
            }

            if (Arguments != null)
            {
                foreach (string key in Arguments.Keys.ToList())
                {
                    Arguments[key] = MigrateFieldIfLegacy(Arguments[key]);
                }
            }

            Data = MigrateFieldIfLegacy(Data);

            URL = MigrateFieldIfLegacy(URL);
            ThumbnailURL = MigrateFieldIfLegacy(ThumbnailURL);
            DeletionURL = MigrateFieldIfLegacy(DeletionURL);
            ErrorMessage = MigrateFieldIfLegacy(ErrorMessage);
        }

        // The complete legacy ShareX custom-uploader function vocabulary. Only these $func[:args]$ tokens
        // are converted; every other '$' is left intact. Keep in sync with the Functions/ directory.
        private static readonly System.Text.RegularExpressions.Regex LegacyFunctionToken =
            new(@"\$(json|xml|regex|response|responseurl|header|input|inputbox|prompt|outputbox|base64|random|select|filename)(:[^$]*)?\$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Converts legacy ShareX <c>$func[:args]$</c> tokens to the modern <c>{func[:args]}</c> form,
        /// rewriting ONLY recognized function tokens and leaving every other <c>$</c> untouched (currency,
        /// custom placeholders, regex anchors, already-modern <c>{...}</c>). This is idempotent and never
        /// corrupts literal dollar signs — unlike a blind brace toggle over the whole string.
        /// </summary>
        private static string MigrateFieldIfLegacy(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            return LegacyFunctionToken.Replace(input, static m => "{" + m.Groups[1].Value + m.Groups[2].Value + "}");
        }

        private void CheckRequestURL()
        {
            if (!string.IsNullOrEmpty(RequestURL))
            {
                NameValueCollection? nvc = URLHelpers.ParseQueryString(RequestURL);

                if (nvc != null && nvc.Count > 0)
                {
                    if (Parameters == null)
                    {
                        Parameters = new Dictionary<string, string>();
                    }

                    foreach (string key in nvc)
                    {
                        if (key == null)
                        {
                            string[]? values = nvc.GetValues(key);
                            if (values != null)
                            {
                                foreach (string value in values)
                                {
                                    if (!Parameters.ContainsKey(value))
                                    {
                                        Parameters.Add(value, "");
                                    }
                                }
                            }
                        }
                        else if (!Parameters.ContainsKey(key))
                        {
                            string? value = nvc[key];
                            if (!string.IsNullOrEmpty(value))
                            {
                                Parameters.Add(key, value);
                            }
                        }
                    }

                    RequestURL = URLHelpers.RemoveQueryString(RequestURL);
                }
            }
        }
    }
}


