<script lang='ts'>
    interface Props {
        icon: string;
        size?: string;
        ariaLabel?: string;
        inline?: boolean;
    }

    let {
        icon,
        size,
        ariaLabel,
        inline = false,
    }: Props = $props();
</script>

<span
    class='icon-container'
    class:inline={inline}
    style:--icon='var(--icon-{icon})'
    style:--icon-size={size}
>
    <span class='icon' aria-label={ariaLabel}></span>
</span>

<style lang='scss'>
    .icon-container {
        display: flex;
        justify-content: center;
        align-items: center;
    }

    .icon-container.inline {
        display: inline-flex;
    }

    .icon {
        display: inline-flex;
        justify-content: center;
        align-items: center;
        height: calc(var(--pico-line-height) * 1rem);

        &::after {
            display: inline-block;
            content: '';
            width: var(--icon-size, 1rem);
            height: var(--icon-size, 1rem);

            background-color: currentColor;

            mask-image: var(--icon);
            mask-size: var(--icon-size, 1rem);
            mask-repeat: no-repeat;
            mask-position: center;
        }
    }
</style>
