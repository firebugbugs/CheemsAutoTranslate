package com.cheems.translator.mobile.monitor

import android.accessibilityservice.AccessibilityService
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.Rect
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.LinearLayout
import android.widget.TextView

/** Passive overlay: it never receives touch or keyboard focus. */
internal class TranslationOverlay(private val service: AccessibilityService) {
    private val windowManager = service.getSystemService(WindowManager::class.java)
    private var card: LinearLayout? = null
    private var contentView: TextView? = null
    private var params: WindowManager.LayoutParams? = null

    fun show(message: String, anchor: Rect, isError: Boolean = false) {
        val view = card ?: createCard().also(::attach)
        contentView?.apply {
            text = message
            setTextColor(if (isError) COLOR_ERROR else COLOR_TEXT)
        }
        position(view, anchor)
    }

    fun hide() {
        val view = card ?: return
        runCatching { windowManager.removeView(view) }
        card = null
        contentView = null
        params = null
    }

    private fun createCard(): LinearLayout {
        val outer = LinearLayout(service).apply {
            orientation = LinearLayout.VERTICAL
            background = roundedBackground()
            elevation = dp(8).toFloat()
            setPadding(dp(14), dp(10), dp(14), dp(11))
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            contentDescription = null
        }
        val title = TextView(service).apply {
            text = "Cheems 翻译"
            setTextColor(COLOR_BRAND)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
            typeface = Typeface.DEFAULT_BOLD
        }
        val result = TextView(service).apply {
            setTextColor(COLOR_TEXT)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            maxLines = 6
            setLineSpacing(dp(2).toFloat(), 1f)
            includeFontPadding = false
            setPadding(0, dp(4), 0, 0)
        }
        outer.addView(title, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT))
        outer.addView(result, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT))
        card = outer
        contentView = result
        return outer
    }

    private fun attach(view: LinearLayout) {
        val layout = WindowManager.LayoutParams(
            dp(320),
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = dp(16)
            y = dp(80)
        }
        params = layout
        runCatching { windowManager.addView(view, layout) }
            .onFailure {
                card = null
                contentView = null
                params = null
            }
    }

    private fun position(view: View, anchor: Rect) {
        val layout = params ?: return
        view.post {
            val metrics = service.resources.displayMetrics
            val screenWidth = metrics.widthPixels
            val screenHeight = metrics.heightPixels
            val width = view.width.takeIf { it > 0 } ?: dp(320)
            val height = view.height.takeIf { it > 0 } ?: dp(88)
            val horizontalCenter = if (anchor.isEmpty) screenWidth / 2 else anchor.centerX()
            val maxX = (screenWidth - width - dp(8)).coerceAtLeast(dp(8))
            layout.x = (horizontalCenter - width / 2).coerceIn(dp(8), maxX)

            val above = anchor.top - height - dp(10)
            val below = anchor.bottom + dp(10)
            val topInset = dp(28)
            val maxY = (screenHeight - height - dp(8)).coerceAtLeast(topInset)
            layout.y = when {
                anchor.isEmpty -> maxY.coerceAtMost(dp(120))
                above >= topInset -> above
                below <= maxY -> below
                else -> topInset
            }.coerceIn(topInset, maxY)
            runCatching { windowManager.updateViewLayout(view, layout) }
        }
    }

    private fun roundedBackground() = GradientDrawable().apply {
        setColor(Color.WHITE)
        cornerRadius = dp(14).toFloat()
        setStroke(dp(1), COLOR_BORDER)
    }

    private fun dp(value: Int): Int = (value * service.resources.displayMetrics.density).toInt()

    companion object {
        private const val COLOR_BRAND = 0xFF176FC1.toInt()
        private const val COLOR_TEXT = 0xFF18212D.toInt()
        private const val COLOR_ERROR = 0xFFAA4C1D.toInt()
        private const val COLOR_BORDER = 0xFFE4EAF0.toInt()
    }
}
