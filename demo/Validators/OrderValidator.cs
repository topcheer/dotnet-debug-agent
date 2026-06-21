using FluentValidation;

namespace DemoApi;

/// <summary>
/// FluentValidation validator for order creation.
/// </summary>
public class OrderCreateValidator : AbstractValidator<OrderCreateDto>
{
    public OrderCreateValidator()
    {
        RuleFor(x => x.Customer)
            .NotEmpty().WithMessage("Customer is required")
            .MaximumLength(100);

        RuleFor(x => x.Item)
            .NotEmpty().WithMessage("Item is required")
            .MaximumLength(200);

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be at least 1")
            .LessThanOrEqualTo(999).WithMessage("Quantity cannot exceed 999");

        RuleFor(x => x.Price)
            .GreaterThan(0).WithMessage("Price must be positive")
            .LessThan(1000000).WithMessage("Price cannot exceed 1,000,000");
    }
}
