using System.ComponentModel.DataAnnotations;

namespace NCash.Application.Modules.Auth.DTOs;

public record RegisterRequestDto(
    [Required, MinLength(2), MaxLength(100)] string FullName,
    [Required, MinLength(3), MaxLength(50), RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Username can only contain alphanumeric characters, dots, underscores, and dashes.")] string Username,
    [Required, EmailAddress, MaxLength(150)] string Email,
    [Required, Phone, MaxLength(25)] string PhoneNumber,
    [Required, MinLength(6), MaxLength(100)] string Password,
    [RegularExpression(@"^\d{4,6}$", ErrorMessage = "Transaction PIN must be 4 to 6 numeric digits.")] string? InitialTransactionPin = null);

public record LoginRequestDto(
    [Required, MinLength(3)] string UsernameOrEmail,
    [Required, MinLength(4)] string Password);

public record AuthResponseDto(
    string Token,
    Guid UserId,
    string FullName,
    string Username,
    string Email,
    string Role,
    Guid AccountId,
    string AccountNumber,
    decimal Balance,
    string Currency,
    bool HasPinConfigured = false);

public record CurrentUserResponseDto(
    Guid UserId,
    string FullName,
    string Username,
    string Email,
    string PhoneNumber,
    string Role,
    string Status,
    Guid AccountId,
    string AccountNumber,
    decimal Balance,
    string Currency,
    bool HasPinConfigured,
    DateTime MemberSinceUtc);

public record SetPinRequestDto(
    [Required, RegularExpression(@"^\d{4,6}$", ErrorMessage = "PIN must be between 4 and 6 numeric digits.")] string Pin,
    [Required, RegularExpression(@"^\d{4,6}$", ErrorMessage = "Confirm PIN must match the 4-6 digit numeric format.")] string ConfirmPin);

public record VerifyPinRequestDto(
    [Required, RegularExpression(@"^\d{4,6}$", ErrorMessage = "PIN must be 4 to 6 numeric digits.")] string Pin);

public record PinOperationResultDto(
    bool Success,
    string Message);
