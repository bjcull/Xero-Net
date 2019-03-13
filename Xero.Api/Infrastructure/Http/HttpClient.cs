﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using Serilog;
using Xero.Api.Infrastructure.Interfaces;
using Xero.Api.Infrastructure.RateLimiter;

namespace Xero.Api.Infrastructure.Http
{
    // This has enough functionality to get the call the API.
    // Content and accept types are defaulted and it is always ask for the response to be compressed.
    // Json for GET and XML for PUT and POST
    // It uses IAuthenticator or ICertificateAuthenticator to do the signing
    internal class HttpClient
    {
        static readonly int defaultTimeout = (int)TimeSpan.FromMinutes(5.5).TotalMilliseconds;

        private readonly string _baseUri;
        private readonly IAuthenticator _auth;
        private readonly IRateLimiter _rateLimiter;

        private readonly Dictionary<string, string> _headers;

        public DateTime? ModifiedSince { get; set; }
        public IUser User { get; set; }
        
        private IConsumer Consumer { get; set; }

        public HttpClient(string baseUri)
        {
            _baseUri = baseUri;
            _headers = new Dictionary<string, string>();
        }
        
        public HttpClient(string baseUri, IConsumer consumer, IUser user) : this(baseUri)
        {
            User = user;
            Consumer = consumer;
        }

        public HttpClient(string baseUri, IAuthenticator auth, IConsumer consumer, IUser user)
            : this(baseUri, consumer, user)
        {
            _auth = auth;
        }

        public HttpClient(string baseUri, IAuthenticator auth, IConsumer consumer, IUser user, IRateLimiter rateLimiter)
            : this(baseUri, auth, consumer, user)
        {
            _rateLimiter = rateLimiter;
        }

        public string UserAgent
        {
            get; set;
        }

        public Response Post(string endpoint, string data, string contentType = "application/xml", string query = null)
        {
            return Post(endpoint, Encoding.UTF8.GetBytes(data), contentType, query);
        }
        
        public Response Post(string endpoint, byte[] data, string contentType = "application/xml", string query = null)
        {
            try
            {
                return WriteToServer(endpoint, data, "POST", contentType, query);
            }
            catch (WebException we)
            {
	            if (we.Response != null)
	            {
		            return new Response((HttpWebResponse) we.Response);
	            }

	            throw;
            }
        }
            
        public Response PostMultipartForm(string endpoint, string contentType, string name, string filename, byte[] payload)
        {
            return WriteToServerWithMultipart(endpoint, contentType, name,filename, payload);
        }

        public Response Put(string endpoint, string data, string contentType = "application/xml", string query = null)
        {
            try
            {
                return WriteToServer(endpoint, Encoding.UTF8.GetBytes(data), "PUT", contentType, query);
            }
            catch (WebException we)
            {
	            if (we.Response != null)
	            {
		            return new Response((HttpWebResponse) we.Response);
	            }

	            throw;
            }
        }

        public Response Get(string endpoint, string query)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var request = CreateRequest(endpoint, "GET", query: query);
                var response = new Response((HttpWebResponse)request.GetResponse());
                
                var finish = Stopwatch.GetTimestamp();

                LogHttp("GET", endpoint + query, null, (int) response.StatusCode, response.Body, start, finish);

                return response;
            }
            catch (WebException we)
            {
                LogHttpFailure("GET", endpoint + query, null, we, start);

	            if (we.Response != null)
	            {
		            return new Response((HttpWebResponse) we.Response);
	            }

	            throw;
            }
        }

        public Response GetRaw(string endpoint, string mimeType, string query = null)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var request = CreateRequest(endpoint, "GET", mimeType, query);
                var response = new Response((HttpWebResponse)request.GetResponse());
                
                var finish = Stopwatch.GetTimestamp();

                LogHttp("GET", endpoint + query, null, (int) response.StatusCode, response.Body, start, finish);

                return response;
            }
            catch (WebException we)
            {
                LogHttpFailure("GET", endpoint + query, null, we, start);

	            if (we.Response != null)
	            {
		            return new Response((HttpWebResponse) we.Response);
	            }

	            throw;
            }
        }

        public Response Delete(string endpoint)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var request = CreateRequest(endpoint, "DELETE");
                var response = new Response((HttpWebResponse) request.GetResponse());

                var finish = Stopwatch.GetTimestamp();

                LogHttp("DELETE", endpoint, null, (int) response.StatusCode, response.Body, start, finish);

                return response;
            }
            catch (WebException we)
            {
                LogHttpFailure("DELETE", endpoint, null, we, start);

                if (we.Response != null)
                {
                    return new Response((HttpWebResponse) we.Response);
                }

                throw;
            }
        }

        private HttpWebRequest CreateRequest(string endPoint, string method, string accept = "application/json", string query = null)
        {
            var uri = new UriBuilder(_baseUri)
            {
                Path = endPoint,
            };

            if (!string.IsNullOrWhiteSpace(query))
            {
                uri.Query = query;
            }

            var request = (HttpWebRequest)WebRequest.Create(uri.Uri);

            request.Timeout = defaultTimeout;

            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            
            request.Accept = accept;
            request.Method = method;

            if (ModifiedSince.HasValue)
            {
                request.IfModifiedSince = ModifiedSince.Value;
            }

            if (_auth != null)
            {
                var oauthSignature = _auth.GetSignature(Consumer, User, request.RequestUri, method, Consumer);

                AddHeader("Authorization", oauthSignature);
            }
            
            AddHeaders(request);

            request.UserAgent = !string.IsNullOrWhiteSpace(UserAgent) ? UserAgent : "Xero Api wrapper - " + Consumer.ConsumerKey;
            
            if (_rateLimiter != null)
                _rateLimiter.WaitUntilLimit();

            return request;
        }

        private void AddHeaders(WebRequest request)
        {
            foreach (var pair in _headers)
            {
                request.Headers.Add(pair.Key, pair.Value);
            }
        }

        public void AddHeader(string name, string value)
        {
            _headers[name] = value;
        }

        private static void WriteData(byte[] bytes, WebRequest request, string contentType)
        {
            request.ContentLength = bytes.Length;
            request.ContentType = contentType;

            using (var dataStream = request.GetRequestStream())
            {
                dataStream.Write(bytes, 0, bytes.Length);
            }
        }

        private Response WriteToServerWithMultipart(string endpoint,string contentType, string name, string filename ,byte[] payload)
        {
            var request = CreateRequest(endpoint, "POST");

            WriteMultipartData(payload, request, contentType,name, filename);
            
            return new Response((HttpWebResponse)request.GetResponse());
        }

        private void WriteMultipartData(byte[] bytes, HttpWebRequest request, string contentType, string name, string filename)
        {
            var boundary = Guid.NewGuid();

            byte[] header = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\nContent-Disposition: form-data; name=" + name + "; FileName=" + filename + " \r\nContent-Type: " + contentType + "\r\n\r\n");
            byte[] trailer = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.KeepAlive = false;
            
            var contentLength = bytes.Length + header.Length + trailer.Length;
            
            request.ContentLength = contentLength;

            var dataStream = request.GetRequestStream();
            dataStream.Write(header, 0, header.Length);
            dataStream.Write(bytes, 0, bytes.Length);
            dataStream.Write(trailer, 0, trailer.Length);
            dataStream.Close();
        }

        private Response WriteToServer(string endpoint, byte[] data, string method, string contentType = "application/xml", string query = null)
        {
            var start = Stopwatch.GetTimestamp();

            var request = CreateRequest(endpoint, method, query: query);

            string requestBody = null;            
            if (contentType == "application/xml" || contentType == "application/json")
            {
                requestBody = Encoding.UTF8.GetString(data);
            }

            WriteData(data, request, contentType);
            var response = new Response((HttpWebResponse)request.GetResponse());
            var finish = Stopwatch.GetTimestamp();

            LogHttp(method, endpoint + query, requestBody, (int) response.StatusCode, response.Body, start, finish);

            return response;
        }       
        
        private void LogHttp(string requestMethod, string requestUri, string requestBody, int responseCode, string responseBody, long start, long finish)
        {           
            var context = Log.Logger.ForContext<HttpClient>();

            if (!string.IsNullOrEmpty(requestBody))
            {
                context = context.ForContext("RequestBody", requestBody);
            }
            
            context = context.ForContext("ResponseBody", responseBody);

            var elapsed = (finish - start) * 1000 / (double)Stopwatch.Frequency;

            context.Debug(MessageTemplate,
                "Xero", requestMethod, requestUri, responseCode, elapsed);
        }

        private void LogHttpFailure(string requestMethod, string requestUri, string requestBody, WebException ex, long start)
        {
            var context = Log.Logger.ForContext<HttpClient>();                

            if (!string.IsNullOrEmpty(requestBody))
            {
                context = context.ForContext("RequestBody", requestBody);
            }

            var elapsed = (Stopwatch.GetTimestamp() - start) * 1000 / (double)Stopwatch.Frequency;

            context.Error(ex, MessageTemplate,
                "Xero", requestMethod, requestUri, 0, elapsed);
        }

        const string MessageTemplate = "Dependency {Dependency} HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    }
}
