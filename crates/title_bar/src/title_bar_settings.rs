use gpui::WindowButtonLayout;
use settings::{RegisterSetting, Settings, SettingsContent};

#[derive(Copy, Clone, Debug, RegisterSetting)]
pub struct TitleBarSettings {
    pub show_branch_status_icon: bool,
    pub show_onboarding_banner: bool,
    pub show_user_picture: bool,
    pub show_branch_name: bool,
    pub show_worktree_name: bool,
    pub show_project_items: bool,
    pub show_sign_in: bool,
    pub show_user_menu: bool,
    pub show_menus: bool,
    pub open_menus_on_hover: bool,
    pub button_layout: Option<WindowButtonLayout>,
}

impl Settings for TitleBarSettings {
    fn from_settings(s: &SettingsContent) -> Self {
        let content = s.title_bar.clone().unwrap();
        TitleBarSettings {
            show_branch_status_icon: content.show_branch_status_icon.unwrap(),
            show_onboarding_banner: !settings::STANDALONE
                && content.show_onboarding_banner.unwrap(),
            show_user_picture: !settings::STANDALONE && content.show_user_picture.unwrap(),
            show_branch_name: content.show_branch_name.unwrap(),
            show_worktree_name: content.show_worktree_name.unwrap(),
            show_project_items: content.show_project_items.unwrap(),
            show_sign_in: !settings::STANDALONE && content.show_sign_in.unwrap(),
            show_user_menu: !settings::STANDALONE && content.show_user_menu.unwrap(),
            show_menus: content.show_menus.unwrap(),
            open_menus_on_hover: content.open_menus_on_hover.unwrap(),
            button_layout: content.button_layout.unwrap_or_default().into_layout(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[gpui::test]
    fn test_standalone_hides_account_controls_despite_user_settings(cx: &mut gpui::App) {
        if !settings::STANDALONE {
            return;
        }
        let mut store = settings::SettingsStore::test(cx);
        store.set_user_settings(r#"{"title_bar":{"show_sign_in":true,"show_user_menu":true,"show_user_picture":true,"show_onboarding_banner":true,"show_branch_name":true}}"#, cx).expect("valid account settings");
        let settings = TitleBarSettings::from_settings(store.merged_settings());
        assert!(!settings.show_sign_in);
        assert!(!settings.show_user_menu);
        assert!(!settings.show_user_picture);
        assert!(!settings.show_onboarding_banner);
        assert!(settings.show_branch_name);
    }
}
