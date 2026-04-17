import { AuthError } from "@azure/msal-browser";
export declare const ReactAuthErrorMessage: {
    invalidInteractionType: {
        code: string;
        desc: string;
    };
    unableToFallbackToInteraction: {
        code: string;
        desc: string;
    };
};
export declare class ReactAuthError extends AuthError {
    constructor(errorCode: string, errorMessage?: string);
    static createInvalidInteractionTypeError(): ReactAuthError;
    static createUnableToFallbackToInteractionError(): ReactAuthError;
}
