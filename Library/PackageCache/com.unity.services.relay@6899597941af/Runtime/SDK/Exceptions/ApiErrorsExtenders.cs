using System;
using System.Collections.Generic;
using Unity.Services.Relay.Http;
using Unity.Services.Relay.Models;
using KeyValuePair = Unity.Services.Relay.Models.KeyValuePair;

namespace Unity.Services.Relay
{
    internal static class ApiErrorExtender
    {
        public static RelayExceptionReason GetExceptionReason(this ErrorResponseBody error)
        {
            RelayExceptionReason reason = RelayExceptionReason.Unknown;

            if (error.Code != (int)RelayExceptionReason.NoError)
            {
                if (Enum.IsDefined(typeof(RelayExceptionReason), error.Code))
                {
                    reason = (RelayExceptionReason)error.Code;
                }
            }
            else if (Enum.IsDefined(typeof(RelayExceptionReason), error.Status))
            {
                reason = (RelayExceptionReason)error.Status;
            }

            return reason;
        }

        public static RelayExceptionReason GetExceptionReason(this HttpClientResponse error)
        {
            RelayExceptionReason reason = RelayExceptionReason.Unknown;

            if (error.IsHttpError)
            {
                //As we know it's a http error (error range 0-1000), we bump it to our mapped range
                int mappedCode = (int)error.StatusCode + (int)RelayExceptionReason.Min;
                if (Enum.IsDefined(typeof(RelayExceptionReason), mappedCode))
                {
                    reason = (RelayExceptionReason)mappedCode;
                }
            }
            else if (error.IsNetworkError)
            {
                reason = RelayExceptionReason.NetworkError;
            }

            return reason;
        }

        public static string GetExceptionMessage(this ErrorResponseBody error)
        {
            string message = $"{error.Title}: {error.Detail}";
            foreach (KeyValuePair kvp in error.Details ?? new List<KeyValuePair>())
            {
                message += $"\n{kvp.Key}: {kvp.Value}";
            }

            return message;
        }
    }
}
