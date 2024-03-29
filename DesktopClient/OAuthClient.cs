﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DesktopClient
{
    public class OAuthClient
    {
        public Nullable<int> ClientId;
        public string ClientSecret;
        public string RedirectUrl;

        public string Error;
        public string Token;

        const string AuthorizationEndpoint = "https://theorder.gg/oauth/authorize";
        const string TokenEndpoint = "https://theorder.gg/oauth/token";        

        public OAuthClient(int inputClientId, string inputClientSecret, string inputRedirectUrl = "http://localhost:9000/")
        {
            ClientId = inputClientId;
            ClientSecret = inputClientSecret;
            RedirectUrl = inputRedirectUrl;
        }

        public async Task<string> AuthenticateAsync()
        {
            if (ClientId.HasValue
                && ClientSecret.Length > 0
                && RedirectUrl.Length > 0)
            {
                // Set up the HTTP listener...
                HttpListener httpListener = new HttpListener();
                httpListener.Prefixes.Add(RedirectUrl);
                httpListener.Start();

                // Create the OAuth 2.0 authorization request...
                string authorizationRequest = string.Format("{0}?response_type=code&redirect_uri={1}&client_id={2}&scope=&state={3}",
                    AuthorizationEndpoint,
                    System.Uri.EscapeDataString(RedirectUrl),
                    ClientId,
                    GenerateStateValue(32) // Random state value...
                );

                // Open request in the browser...
                System.Diagnostics.Process.Start(authorizationRequest);

                // Wait for the OAuth authorization response...
                var httpResponse = await httpListener.GetContextAsync();

                // Send an HTTP response to the browser...
                string responseString = string.Format("<html><head><title>Authentication</title></head><body><p>Please return to the app. You may now close this tab/window.</p></body></html>");
                var buffer1 = System.Text.Encoding.UTF8.GetBytes(responseString);
                httpResponse.Response.ContentLength64 = buffer1.Length;
                var responseOutput = httpResponse.Response.OutputStream;
                Task responseTask = responseOutput.WriteAsync(buffer1, 0, buffer1.Length).ContinueWith((task) =>
                {
                    responseOutput.Close();
                    httpListener.Stop();
                });

                // Check for errors...
                if (httpResponse.Request.QueryString.Get("error") != null)
                {
                    Error = String.Format("OAuth error: {0}.", httpResponse.Request.QueryString.Get("error"));
                    return null;
                }
                if (httpResponse.Request.QueryString.Get("code") == null)
                {
                    Error = "Malformed response received.";
                    return null;
                }

                // Extract the authentication code...
                return httpResponse.Request.QueryString.Get("code");
            }

            return null;
        }

        public async Task<string> GetAccessTokenAsync(string authenticationCode)
        {
            // Exchange the authentication code for an authorization token...
            string formDataBoundary = "----------------------------" + DateTime.Now.Ticks.ToString("x");

            // Prepare the HTTP request...
            HttpWebRequest tokenRequest = (HttpWebRequest)WebRequest.Create(TokenEndpoint);
            tokenRequest.Method = "POST";
            tokenRequest.ContentType = "multipart/form-data; boundary=" + formDataBoundary;
            tokenRequest.Accept = "application/json";

            // Build the request body...
            Stream formDataStream = new MemoryStream();         
            string formDataTemplate =
                    Environment.NewLine + "--" + formDataBoundary +
                    Environment.NewLine + "Content-Disposition: form-data; name=\"{0}\";" +
                    Environment.NewLine + Environment.NewLine + "{1}";

            // Grant Type...
            byte[] formDataGrantType = Encoding.UTF8.GetBytes(string.Format(formDataTemplate, "grant_type", "authorization_code"));
            formDataStream.Write(formDataGrantType, 0, formDataGrantType.Length);

            // Client ID...
            byte[] formDataClientId = Encoding.UTF8.GetBytes(string.Format(formDataTemplate, "client_id", ClientId));
            formDataStream.Write(formDataClientId, 0, formDataClientId.Length);

            // Client Secret...
            byte[] formDataClientSecret = Encoding.UTF8.GetBytes(string.Format(formDataTemplate, "client_secret", ClientSecret));
            formDataStream.Write(formDataClientSecret, 0, formDataClientSecret.Length);

            // Redirect URI...
            byte[] formDataRedirectUri = Encoding.UTF8.GetBytes(string.Format(formDataTemplate, "redirect_uri", RedirectUrl));
            formDataStream.Write(formDataRedirectUri, 0, formDataRedirectUri.Length);

            // Code...
            byte[] formDataCode = Encoding.UTF8.GetBytes(string.Format(formDataTemplate, "code", authenticationCode));
            formDataStream.Write(formDataCode, 0, formDataCode.Length);

            // Set the request length...
            tokenRequest.ContentLength = formDataStream.Length;

            // Set the request...
            Stream tokenRequestStream = tokenRequest.GetRequestStream();
            formDataStream.Position = 0;
            byte[] buffer2 = new byte[1024];
            int bytesRead = 0;
            while ((bytesRead = formDataStream.Read(buffer2, 0, buffer2.Length)) != 0)
            {
                tokenRequestStream.Write(buffer2, 0, bytesRead);
            }
            
            // Cleanup...
            formDataStream.Close();
            tokenRequestStream.Close();

            try
            {
                // Send the request...
                WebResponse tokenResponse = await tokenRequest.GetResponseAsync();

                using (StreamReader tokenResponseStreamReader = new StreamReader(tokenResponse.GetResponseStream()))
                {
                    // Read the entire response body...
                    string responseBody = await tokenResponseStreamReader.ReadToEndAsync();

                    // Convert the response body to a dictionary...
                    Dictionary<string, string> tokenEndpointDecoded = JsonConvert.DeserializeObject<Dictionary<string, string>>(responseBody);

                    // Catch the token...
                    Token = tokenEndpointDecoded["access_token"];

                    // Save the token to the settings...
                    Properties.Settings.Default.accessToken = Token;
                    Properties.Settings.Default.Save();
                }
            }
            catch (WebException e)
            {
                if (e.Status == WebExceptionStatus.ProtocolError && e.Response != null)
                {
                    var errorResponse = e.Response as HttpWebResponse;
                    using (StreamReader errorResponseReader = new StreamReader(errorResponse.GetResponseStream()))
                    {
                        Error = string.Format(
                            "HTTP {0}: {1} ", 
                            errorResponse.StatusCode,
                            await errorResponseReader.ReadToEndAsync()
                        );
                    } 
                }
            }

            // return the access token. If authentication failed this will be null...
            return Token;
        }

        private static string GenerateStateValue(uint length)
        {
            RNGCryptoServiceProvider rngProvider = new RNGCryptoServiceProvider();
            byte[] bytes = new byte[length];
            rngProvider.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        private static string Base64UrlEncode(byte[] buffer)
        {
            string base64 = Convert.ToBase64String(buffer);

            // Converts base64 to base64url...
            base64 = base64.Replace("+", "-");
            base64 = base64.Replace("/", "_");

            // Strips padding...
            base64 = base64.Replace("=", "");

            return base64;
        }

        public bool IsAuthenticated()
        {
            return ! string.IsNullOrEmpty(Token);
        }
    }
}
