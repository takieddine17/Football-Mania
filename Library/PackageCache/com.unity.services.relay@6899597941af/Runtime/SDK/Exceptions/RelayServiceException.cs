using System;
using Unity.Services.Core;
using Unity.Services.Relay.Http;
using Unity.Services.Relay.Models;

namespace Unity.Services.Relay
{
    /// <summary>
    /// Represents an exception that occurs when communicating with the Unity Relay Service.
    /// </summary>
    public class RelayServiceException : RequestFailedException
    {
        /// <summary>
        /// The reason of the exception.
        /// </summary>
        public RelayExceptionReason Reason { get; private set; }

        /// <summary>
        /// If applicable, the specific details of the API error that caused the exception.
        /// </summary>
        public ErrorResponseBody ApiError
        {
            get
            {
                HttpException<ErrorResponseBody> apiException = InnerException as HttpException<ErrorResponseBody>;
                return apiException?.ActualError;
            }
        }

        /// <summary>
        /// Creates a RelayServiceException.
        /// </summary>
        /// <param name="reason">The error code or the HTTP Status returned by the service.</param>
        /// <param name="message">The description of the exception.</param>
        /// <param name="innerException">The exception raised by the service, if any.</param>
        public RelayServiceException(RelayExceptionReason reason, string message, Exception innerException) : base((int)reason, message, innerException)
        {
            Reason = reason;
        }

        /// <summary>
        /// Creates a RelayServiceException.
        /// </summary>
        /// <param name="reason">The error code or the HTTP Status returned by the service.</param>
        /// <param name="message">The description of the exception.</param>
        public RelayServiceException(RelayExceptionReason reason, string message) : base((int)reason, message)
        {
            Reason = reason;
        }

        /// <summary>
        /// Creates a RelayServiceException.
        /// </summary>
        /// <param name="errorCode">The error code or the HTTP Status returned by the service.</param>
        /// <param name="message">The description of the exception.</param>
        public RelayServiceException(long errorCode, string message) : base((int)errorCode, message)
        {
            if (Enum.IsDefined(typeof(RelayExceptionReason), errorCode))
            {
                Reason = (RelayExceptionReason)errorCode;
            }
            else
            {
                Reason = RelayExceptionReason.Unknown;
            }
        }

        /// <summary>
        /// Creates a RelayServiceException.
        /// </summary>
        /// <param name="innerException">The exception raised by the service, if any.</param>
        public RelayServiceException(Exception innerException) : base((int)RelayExceptionReason.Unknown, "Unknown Relay Service Exception", innerException)
        {
        }
    }
}
