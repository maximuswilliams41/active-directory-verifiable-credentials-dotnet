using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AspNetCoreVerifiableCredentials
{
    public enum AyaIssuanceStage
    {
        Configuration, Identity, StorageRead, EmployeeClaims, RequestCreation, StateBegin,
        TokenAcquisition, ServiceRequestPreparation, ServiceRequest, ServiceResponse, ResponseValidation, StateBinding, OutcomeRecording
    }
    public enum AyaIssuanceReason
    {
        Started, RequestCreated, IssuanceDisabled, ValidityUnconfigured, RequestNotConfigured,
        IdentityDenied, EmployeeNotEligible, IncompleteEmployeeProfile, PreSubmissionConflict,
        RequestAlreadyActive, StateCapacityReached, StateIdConflict, StateArgumentsInvalid,
        TokenUnavailable, TokenAcquisitionFailed, ServiceRequestPreparationFailed, ServiceTransportFailed, ServiceRejected,
        ServiceResponseFailed, ResponseValidationFailed, StateBindingFailed,
        EmployeeClaimsFailed, StorageUnavailable, UnexpectedFailure
    }

    // This boundary accepts only closed enums, HTTP codes, and a freshly generated ID.
    // Never pass exception objects, service IDs, claims, headers or payloads to the logger.
    internal sealed class AyaIssuanceDiagnostic : IDisposable
    {
        private readonly ILogger _log;
        private readonly string _id = Guid.NewGuid().ToString("N");
        private AyaIssuanceReason _reason = AyaIssuanceReason.UnexpectedFailure;
        private int _httpStatus = 500;
        public AyaIssuanceStage Stage { get; set; } = AyaIssuanceStage.Configuration;
        public int? ServiceHttpStatus { get; set; }
        public AyaResponseValidationReason ValidationReason { get; set; }
        public AyaIssuanceDiagnostic(ILogger log, HttpResponse response)
        {
            _log = log;
            response.Headers["X-Aya-Issuance-Correlation-ID"] = _id;
            Write(AyaIssuanceReason.Started, null, LogLevel.Information);
        }
        public void Outcome(AyaIssuanceReason reason, int httpStatus) { _reason = reason; _httpStatus = httpStatus; }
        public void Dispose() => Write(_reason, _httpStatus, _httpStatus >= 400 ? LogLevel.Warning : LogLevel.Information);
        private void Write(AyaIssuanceReason reason, int? httpStatus, LogLevel level) =>
            _log.Log(level, new EventId(7200, "AyaIssuance"),
                "Aya issuance {IssuanceCorrelationId} {ReasonCode} stage {Stage} HTTP {HttpStatus} service HTTP {ServiceHttpStatus} validation {ValidationReason}",
                _id, reason, Stage, httpStatus, ServiceHttpStatus is >= 100 and <= 599 ? ServiceHttpStatus : null, ValidationReason);
    }
}
