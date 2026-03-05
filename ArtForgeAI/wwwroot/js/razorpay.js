// Razorpay Checkout JS Interop for Blazor Server
window.razorpayCheckout = function (options) {
    if (typeof Razorpay === 'undefined') {
        console.error('Razorpay SDK not loaded');
        return;
    }

    var rzp = new Razorpay({
        key: options.key,
        amount: options.amount,
        currency: options.currency,
        order_id: options.order_id,
        name: options.name || 'ArtForge AI',
        description: options.description || 'Payment',
        theme: options.theme || { color: '#6366f1' },
        handler: function (response) {
            // Payment successful — call Blazor component
            DotNet.invokeMethodAsync('ArtForgeAI', 'OnRazorpaySuccess',
                options.paymentId,
                response.razorpay_payment_id,
                response.razorpay_signature
            ).catch(function (err) {
                console.error('Blazor callback failed:', err);
            });
        },
        modal: {
            ondismiss: function () {
                console.log('Razorpay checkout dismissed');
            }
        }
    });

    rzp.on('payment.failed', function (response) {
        DotNet.invokeMethodAsync('ArtForgeAI', 'OnRazorpayFailure',
            response.error.description || 'Payment failed'
        ).catch(function (err) {
            console.error('Blazor failure callback failed:', err);
        });
    });

    rzp.open();
};
